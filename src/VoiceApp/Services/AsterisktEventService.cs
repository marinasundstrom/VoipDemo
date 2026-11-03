using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceApp.Services;

public sealed class AsteriskEventService(
    IConfiguration configuration,
    AsteriskClient asterisk,
    ILogger<AsteriskEventService> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, string>
    recordings = new();
    
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Asterisk connection failed");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ListenAsync(
       CancellationToken cancellationToken)
    {
        var baseUrl = configuration["Asterisk:BaseUrl"]
            ?? throw new InvalidOperationException("Missing Asterisk:BaseUrl");

        var username = configuration["Asterisk:Username"]
            ?? throw new InvalidOperationException("Missing Asterisk:Username");

        var password = configuration["Asterisk:Password"]
            ?? throw new InvalidOperationException("Missing Asterisk:Password");

        var uri = new UriBuilder(new Uri(baseUrl))
        {
            Scheme = baseUrl.StartsWith(
                "https://", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };

        uri.Path = uri.Path.TrimEnd('/') + "/events";

        uri.Query =
            $"app=voiceapp&api_key=" +
            Uri.EscapeDataString($"{username}:{password}");

        using var socket = new ClientWebSocket();

        logger.LogInformation(
            "Connecting to Asterisk ARI at {Host}:{Port}",
            uri.Host,
            uri.Port);

        await socket.ConnectAsync(uri.Uri, cancellationToken);

        logger.LogInformation("Connected to Asterisk ARI");

        var buffer = new byte[8192];

        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(
                buffer.AsMemory(),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            message.Write(buffer, 0, result.Count);

            if (!result.EndOfMessage)
                continue;

            message.Position = 0;

            try
            {
                using var json = await JsonDocument.ParseAsync(
                    message,
                    cancellationToken: cancellationToken);

                await HandleEventAsync(
                    json.RootElement,
                    cancellationToken);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid ARI event");
            }
            finally
            {
                message.SetLength(0);
                message.Position = 0;
            }
        }
    }

    private async Task HandleEventAsync(
     JsonElement root,
     CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("type", out var type))
            return;

        switch (type.GetString())
        {
            case "StasisStart":
                await HandleCallStartedAsync(root, cancellationToken);
                break;

            case "RecordingFinished":
                logger.LogInformation(
                    "Recording finished: {Event}",
                    root.GetRawText());

                await HandleRecordingFinishedAsync(
                    root,
                    cancellationToken);
                break;

            case "ChannelDtmfReceived":
                HandleDtmfReceived(root);
                break;

            case "PlaybackFinished":
                logger.LogInformation("Playback finished");
                break;

            case "StasisEnd":
                {
                    var channelId = root
                        .GetProperty("channel")
                        .GetProperty("id")
                        .GetString();

                    logger.LogInformation(
                        "Channel {ChannelId} left Stasis",
                        channelId);

                    break;
                }

            case "ChannelDestroyed":
                {
                    var channelId = root
                        .GetProperty("channel")
                        .GetProperty("id")
                        .GetString();

                    logger.LogInformation(
                        "Channel {ChannelId} destroyed: {Cause}",
                        channelId,
                        root.TryGetProperty("cause_txt", out var cause)
                            ? cause.GetString()
                            : "Unknown");

                    break;
                }
        }
    }

    private async Task HandleCallStartedAsync(
    JsonElement root,
    CancellationToken cancellationToken)
    {
        var channel = root.GetProperty("channel");

        var channelId = channel.GetProperty("id").GetString();

        if (string.IsNullOrEmpty(channelId))
            return;

        var channelName = channel.GetProperty("name").GetString();

        var caller = channel.GetProperty("caller");

        var callerNumber = caller.GetProperty("number").GetString();
        var callerName = caller.GetProperty("name").GetString();

        logger.LogInformation(
            "Incoming call: ChannelId={ChannelId}, Caller={Caller}",
            channelId,
            callerNumber);

        // ...

        try
        {
            await asterisk.AnswerAsync(
                channelId,
                cancellationToken);

            await asterisk.PlayAsync(
                channelId,
                "custom/greeting",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to handle call {ChannelId}",
                channelId);

            return;
        }

        var recordingName = $"echo-{Guid.NewGuid():N}";

        recordings[channelId] = recordingName;

        try
        {
            await asterisk.StartRecordingAsync(
                channelId,
                recordingName,
                cancellationToken);
        }
        catch (Exception ex)
        {
            recordings.TryRemove(channelId, out _);

            logger.LogError(
                ex,
                "Failed to start recording for {ChannelId}",
                channelId);
        }
    }

    private void HandleDtmfReceived(JsonElement root)
    {
        var channelId = root
            .GetProperty("channel")
            .GetProperty("id")
            .GetString();

        var digit = root
            .GetProperty("digit")
            .GetString();

        logger.LogInformation(
            "Caller pressed {Digit} on channel {ChannelId}",
            digit,
            channelId);
    }

    private async Task HandleRecordingFinishedAsync(
    JsonElement root,
    CancellationToken cancellationToken)
    {
        var recording = root.GetProperty("recording");
        var recordingName = recording.GetProperty("name").GetString();

        if (recordingName is null)
            return;

        foreach (var (channelId, name) in recordings)
        {
            if (name != recordingName)
                continue;

            recordings.TryRemove(channelId, out _);

            logger.LogInformation(
                "Playing recording {RecordingName}",
                recordingName);

            await asterisk.PlayRecordingAsync(
                channelId,
                recordingName,
                cancellationToken);

            break;
        }
    }
}