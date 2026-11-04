using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Voip.Telephony;

public sealed class AsteriskEventService(
    IConfiguration configuration,
    AsteriskClient asterisk,
    ICallFlow flow,
    ILogger<AsteriskEventService> logger) : BackgroundService
{
    private string Application => configuration["Asterisk:Application"] ?? "voiceapp";
    private string MediaChannelPrefix => Application + "-media-";
    private readonly ConcurrentDictionary<string, CallSession> sessions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ListenAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Asterisk connection failed"); }
            finally
            {
                // A lost event stream means we can no longer reliably track hangups.
                var active = sessions.Values.ToArray();
                foreach (var session in active) session.Cancel();
                await Task.WhenAll(active.Select(s => s.Completion));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var uri = WebSocketUri();
        uri.Path = uri.Path.TrimEnd('/') + "/events";
        var username = configuration["Asterisk:Username"]
            ?? throw new InvalidOperationException("Missing Asterisk:Username");
        var password = configuration["Asterisk:Password"]
            ?? throw new InvalidOperationException("Missing Asterisk:Password");
        uri.Query = $"app={Uri.EscapeDataString(Application)}&api_key=" + Uri.EscapeDataString($"{username}:{password}");
        using var socket = new ClientWebSocket();
        logger.LogInformation("Connecting to Asterisk ARI at {Host}:{Port}", uri.Host, uri.Port);
        await socket.ConnectAsync(uri.Uri, cancellationToken);
        logger.LogInformation("Connected to Asterisk ARI");
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1024 * 1024) throw new InvalidDataException("ARI event is too large.");
            if (!result.EndOfMessage) continue;
            try
            {
                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                HandleEvent(json.RootElement, cancellationToken);
            }
            catch (JsonException ex) { logger.LogWarning(ex, "Invalid ARI event"); }
            finally { message.SetLength(0); }
        }
    }

    private UriBuilder WebSocketUri()
    {
        var url = configuration["Asterisk:BaseUrl"]
            ?? throw new InvalidOperationException("Missing Asterisk:BaseUrl");
        var uri = new UriBuilder(url);
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        return uri;
    }

    private void HandleEvent(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("type", out var type) ||
            !root.TryGetProperty("channel", out var channel) ||
            !channel.TryGetProperty("id", out var id)) return;
        var channelId = id.GetString();
        if (string.IsNullOrEmpty(channelId)) return;
        switch (type.GetString())
        {
            case "StasisStart":
                var name = channel.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (channelId.StartsWith(MediaChannelPrefix, StringComparison.Ordinal) ||
                    name?.StartsWith("WebSocket/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var owner = sessions.Values.FirstOrDefault(s => s.MediaChannelId == channelId);
                    owner?.MediaEnteredStasis.TrySetResult();
                    return;
                }
                var session = new CallSession { Id = Guid.NewGuid().ToString("N"), ChannelId = channelId };
                if (!sessions.TryAdd(channelId, session))
                {
                    session.DisposeAsync().GetAwaiter().GetResult();
                    return;
                }
                logger.LogInformation("Incoming call: {ChannelId}", channelId);
                // Register synchronously, then let this call own setup, media, and cleanup.
                session.Completion = RunCallAsync(session, cancellationToken);
                break;
            case "StasisEnd":
            case "ChannelDestroyed":
                if (sessions.TryGetValue(channelId, out var active)) active.Cancel();
                // Media channel events must never tear down the caller's session.
                break;
            case "ChannelDtmfReceived":
                if (sessions.TryGetValue(channelId, out var caller))
                    caller.Input.Add(root.GetProperty("digit").GetString());
                break;
        }
    }

    private async Task RunCallAsync(CallSession session, CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.CancellationToken);
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            setup.CancelAfter(TimeSpan.FromSeconds(30));
            var token = setup.Token;
            await asterisk.AnswerAsync(session.ChannelId, token);
            // Assign IDs before REST requests so even a lost response can be cleaned up.
            session.BridgeId = Application + "-bridge-" + session.Id;
            await asterisk.CreateBridgeAsync(session.BridgeId, token);
            await asterisk.AddChannelToBridgeAsync(session.BridgeId, session.ChannelId, token);
            session.MediaChannelId = MediaChannelPrefix + session.Id;
            await asterisk.CreateExternalMediaChannelAsync(session.MediaChannelId, token);
            var connectionId = await asterisk.GetChannelVariableAsync(
                session.MediaChannelId, "MEDIA_WEBSOCKET_CONNECTION_ID", token);
            if (string.IsNullOrWhiteSpace(connectionId))
                throw new InvalidOperationException("Asterisk did not return a media WebSocket connection ID.");
            var mediaUri = WebSocketUri();
            mediaUri.Path = "/media/" + Uri.EscapeDataString(connectionId);
            mediaUri.Query = "";
            session.Media = new AsteriskMediaSession();
            await session.Media.ConnectAsync(mediaUri.Uri, token);
            // Incoming media channels enter Stasis only after the socket connects.
            await session.MediaEnteredStasis.Task.WaitAsync(token);
            await asterisk.AddChannelToBridgeAsync(session.BridgeId, session.MediaChannelId, token);
            logger.LogInformation("Media ready for {ChannelId}", session.ChannelId);
            try
            {
                await flow.RunAsync(session.Media, session.Input, lifetime.Token);
                return;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                logger.LogWarning("Media flow control ended for {ChannelId}", session.ChannelId);
            }
            catch (ChannelClosedException)
            {
                logger.LogWarning("Incoming media ended for {ChannelId}", session.ChannelId);
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "Media connection lost for {ChannelId}", session.ChannelId);
            }
            logger.LogInformation("Media ended for {ChannelId}; waiting for caller hangup", session.ChannelId);
            // A media-leg departure alone must not hang up the caller.
            await Task.Delay(Timeout.Infinite, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Call media failed for {ChannelId}", session.ChannelId); }
        finally
        {
            await session.DisposeAsync();
            if (session.MediaChannelId is not null)
                await CleanupAsync(ct => asterisk.HangupAsync(session.MediaChannelId, ct), "media channel", session.ChannelId);
            if (session.BridgeId is not null)
                await CleanupAsync(ct => asterisk.DestroyBridgeAsync(session.BridgeId, ct), "bridge", session.ChannelId);
            await CleanupAsync(ct => asterisk.HangupAsync(session.ChannelId, ct), "caller channel", session.ChannelId);
            sessions.TryRemove(session.ChannelId, out _);
        }
    }

    private async Task CleanupAsync(Func<CancellationToken, Task> cleanup, string resource, string channelId)
    {
        // Cleanup must run even after the call/host token has been cancelled.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cleanup(timeout.Token); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not remove {Resource} for {ChannelId}", resource, channelId); }
    }

}
