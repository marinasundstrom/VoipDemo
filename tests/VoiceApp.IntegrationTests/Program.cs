using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceApp.Services;

foreach (var scenario in new[] { "tone", "speech", "early-hangup", "setup-failure", "shutdown", "media-exit", "flow-control", "echo", "menu-dtmf" })
{
    await RunAsync(scenario);
    Console.WriteLine($"PASS {scenario}");
}

await CheckInputAsync();
await CheckMenuAsync();
if (OperatingSystem.IsMacOS() && args.Contains("--speech"))
{
    var speech = new MacSpeechSynthesizer(new ConfigurationBuilder().Build());
    var pcm = await speech.SynthesizeAsync("This is live speech generated for the demo.");
    Check(pcm.Length > 3200 && pcm.Length % 2 == 0 && pcm.Any(b => b != 0), "say returned invalid audio");
    Console.WriteLine("PASS macOS live synthesis");
}

static async Task CheckInputAsync()
{
    foreach (var (entry, expected) in new (string, string?)[]
    {
        ("0123456789#", "0123456789"), ("123#", null), ("01234567890#", null),
        ("0123456789", null), ("01234*6789#", null), ("#", null)
    })
    {
        var input = new CallInput();
        foreach (var digit in entry) input.Add(digit.ToString());
        Check(await input.ReadTenDigitsAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None) == expected,
            "Incorrect number validation: " + entry);
    }
    var cancelled = new CallInput();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await cancelled.ReadAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        throw new Exception("Hangup did not cancel input");
    }
    catch (OperationCanceledException) { }
    Console.WriteLine("PASS DTMF validation, leading zeroes, terminator, timeout, cancellation");
}

static async Task CheckMenuAsync()
{
    var input = new CallInput();
    var mainChoices = new Queue<char>("123450");
    var optionChoices = new Queue<char>("92");
    var entries = new Queue<string>(["123#", "0123456789#"]);
    var spoken = new List<string>();
    var speech = new FakeSpeech(text =>
    {
        spoken.Add(text);
        if (text.StartsWith("Welcome")) input.Add(mainChoices.Dequeue().ToString());
        else if (text.StartsWith("Choose an option")) input.Add(optionChoices.Dequeue().ToString());
        else if (text.StartsWith("Enter exactly"))
            foreach (var digit in entries.Dequeue()) input.Add(digit.ToString());
    });
    var file = Path.GetTempFileName();
    try
    {
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Scenario"] = "Menu", ["Demo:RecordingFile"] = file
        }).Build();
        var media = new FakeMedia();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await new DemoCallFlow(config, speech, NullLogger<DemoCallFlow>.Instance).RunAsync(media, input, timeout.Token);
        Check(media.FileBytes?.SequenceEqual(new byte[] { 1, 2, 3, 4 }) == true, "Recording file not played");
        Check(spoken.Any(s => s.Contains("server time is")), "Live text not synthesized");
        Check(spoken.Contains("You selected support."), "Choice not confirmed");
        Check(spoken.Contains("You entered: 0, 1, 2, 3, 4, 5, 6, 7, 8, 9."), "Number not spoken digit by digit");
        Check(media.Recorded && media.EchoPlayed, "Echo scenario did not play captured input");
        Check(spoken.Last() == "Goodbye." && mainChoices.Count == 0, "Menu did not exit cleanly");
        Console.WriteLine("PASS all menu scenarios and invalid-input retries");
    }
    finally { File.Delete(file); }
}

static async Task RunAsync(string scenario)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var ct = deadline.Token;
    var events = Channel.CreateUnbounded<string>();
    var requests = new ConcurrentQueue<string>();
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var mediaConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var server = builder.Build();
    server.UseWebSockets();
    string? mediaId = null;
    string? bridgeId = null;
    var sourceAudio = scenario == "speech" ? Enumerable.Range(0, 1282).Select(i => (byte)i).ToArray() : null;
    var audioPath = Path.GetTempFileName();
    if (sourceAudio is not null) await File.WriteAllBytesAsync(audioPath, sourceAudio, ct);

    server.Map("/ari/events", async context =>
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        connected.TrySetResult();
        try
        {
            await foreach (var message in events.Reader.ReadAllAsync(ct))
                await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });
    server.MapPost("/ari/channels/{id}/answer", (string id) =>
    {
        requests.Enqueue("answer:" + id);
        return Results.NoContent();
    });
    server.MapPost("/ari/bridges/{id}", (string id) =>
    {
        bridgeId = id;
        requests.Enqueue("bridge:" + id);
        return Results.Json(new { id });
    });
    server.MapPost("/ari/bridges/{id}/addChannel", (string id, HttpContext context) =>
    {
        if (context.Request.Query["channel"] != "caller" && !mediaConnected.Task.IsCompleted)
            return Results.UnprocessableEntity();
        requests.Enqueue("add:" + context.Request.Query["channel"]);
        return Results.NoContent();
    });
    server.MapPost("/ari/channels/externalMedia", async (HttpContext context) =>
    {
        var q = context.Request.Query;
        Check(q["transport"] == "websocket" && q["encapsulation"] == "none" &&
              q["format"] == "slin16" && q["external_host"] == "INCOMING" &&
              q["connection_type"] == "server", "Incorrect external media parameters");
        mediaId = q["channelId"].ToString();
        requests.Enqueue("media:" + mediaId);
        // Arrives before the REST response: must not recursively create another call.
        await events.Writer.WriteAsync(Event("StasisStart", mediaId, "WebSocket/test"), ct);
        return Results.Json(new { id = mediaId });
    });
    server.MapGet("/ari/channels/{id}/variable", () =>
        Results.Json(new { value = scenario == "setup-failure" ? "" : "connection" }));
    server.MapDelete("/ari/channels/{id}", (string id) =>
    {
        requests.Enqueue("delete-channel:" + id);
        if (id == "caller") deleted.TrySetResult();
        return Results.NotFound(); // Already-gone resources are normal on hangup.
    });
    server.MapDelete("/ari/bridges/{id}", (string id) =>
    {
        requests.Enqueue("delete-bridge:" + id);
        return Results.NoContent();
    });
    server.Map("/media/connection", async context =>
    {
        Check(context.WebSockets.WebSocketRequestedProtocols.Contains("media"), "Missing media subprotocol");
        using var socket = await context.WebSockets.AcceptWebSocketAsync("media");
        mediaConnected.TrySetResult();
        try
        {
            if (scenario == "early-hangup")
            {
                // The ARI loop must process hangup while MEDIA_START is still pending.
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }
            var start = scenario == "speech"
                ? "{\"event\":\"MEDIA_START\",\"format\":\"slin16\",\"optimal_frame_size\":640}"
                : "MEDIA_START connection_id:connection format:slin16 optimal_frame_size:640";
            if (scenario == "flow-control")
                await socket.SendAsync(Encoding.UTF8.GetBytes("MEDIA_XOFF").AsMemory(), WebSocketMessageType.Text, true, ct);
            var bytes = Encoding.UTF8.GetBytes(start);
            await socket.SendAsync(bytes.AsMemory(0, 10), WebSocketMessageType.Text, false, ct);
            await socket.SendAsync(bytes.AsMemory(10), WebSocketMessageType.Text, true, ct);
            using var audio = new MemoryStream();
            var buffer = new byte[8192];
            var expectedLength = scenario == "menu-dtmf" ? 3200 : sourceAudio is null ? 32000 : 1920;
            if (scenario == "flow-control")
            {
                var pending = socket.ReceiveAsync(buffer.AsMemory(), ct).AsTask();
                await Task.Delay(100, ct);
                Check(!pending.IsCompleted, "Sent audio despite MEDIA_XOFF");
                await socket.SendAsync(Encoding.UTF8.GetBytes("MEDIA_XON").AsMemory(), WebSocketMessageType.Text, true, ct);
                var first = await pending;
                audio.Write(buffer, 0, first.Count);
            }
            while (audio.Length < expectedLength)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                Check(result.MessageType == WebSocketMessageType.Binary, "Expected binary PCM");
                audio.Write(buffer, 0, result.Count);
            }
            if (scenario == "echo")
            {
                var callerFrame = Enumerable.Range(0, 640).Select(i => (byte)(i % 128)).ToArray();
                using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var input = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            await socket.SendAsync(callerFrame.AsMemory(), WebSocketMessageType.Binary, true, inputLifetime.Token);
                            await Task.Delay(20, inputLifetime.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, ct);
                try
                {
                    using var reply = new MemoryStream();
                    while (reply.Length < 6400 + 160000)
                    {
                        var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                        Check(result.MessageType == WebSocketMessageType.Binary, "Expected beep/echo PCM");
                        reply.Write(buffer, 0, result.Count);
                    }
                    var data = reply.ToArray();
                    Check(data.AsSpan(0, 6400).ToArray().Any(b => b != 0), "Missing beep");
                    for (var i = 6400; i < data.Length; i++)
                        Check(data[i] == callerFrame[(i - 6400) % 640], "Echo differs from caller PCM");
                }
                finally
                {
                    await inputLifetime.CancelAsync();
                    await input;
                }
            }
            received.TrySetResult(audio.ToArray());
            if (scenario == "media-exit")
            {
                await events.Writer.WriteAsync(Event("StasisEnd", mediaId!, "WebSocket/test"), ct);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            }
            // Keep the peer alive until caller hangup/host stop aborts the socket.
            while (socket.State == WebSocketState.Open)
                await socket.ReceiveAsync(buffer.AsMemory(), ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    await server.StartAsync(ct);
    var baseUrl = server.Urls.Single() + "/ari/";
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Asterisk:BaseUrl"] = baseUrl,
        ["Asterisk:Username"] = "test",
        ["Asterisk:Password"] = "test",
        ["Asterisk:AudioFile"] = sourceAudio is null ? null : audioPath,
        ["Demo:Scenario"] = scenario == "menu-dtmf" ? "Menu" : "Echo"
    }).Build();
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var menuChoices = new Queue<char>("30");
    var confirmedChoice = false;
    ISpeechSynthesizer synthesizer = new FakeSpeech(text =>
    {
        char? digit = text.StartsWith("Welcome") ? menuChoices.Dequeue()
            : text.StartsWith("Choose an option") ? '2' : null;
        if (text == "You selected support.") confirmedChoice = true;
        if (digit.HasValue)
            events.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                type = "ChannelDtmfReceived", channel = new { id = "caller" }, digit = digit.ToString()
            }));
    });
    using var service = new AsteriskEventService(configuration, new AsteriskClient(http),
        new DemoCallFlow(configuration, synthesizer, NullLogger<DemoCallFlow>.Instance),
        NullLogger<AsteriskEventService>.Instance);
    try
    {
        await service.StartAsync(ct);
        await connected.Task.WaitAsync(ct);
        await events.Writer.WriteAsync(Event("StasisStart", "caller", "PJSIP/test"), ct);
        await events.Writer.WriteAsync(Event("StasisStart", "caller", "PJSIP/test"), ct);
        if (scenario == "early-hangup")
            await mediaConnected.Task.WaitAsync(ct);
        else if (scenario != "setup-failure")
        {
            var audio = await received.Task.WaitAsync(ct);
            if (sourceAudio is not null)
            {
                Check(audio.AsSpan(0, sourceAudio.Length).SequenceEqual(sourceAudio), "Speech bytes changed");
                Check(audio.AsSpan(sourceAudio.Length).ToArray().All(b => b == 0), "Final frame must be silence padded");
            }
            else if (scenario == "menu-dtmf") Check(confirmedChoice, "ARI DTMF did not reach call menu");
            else Check(audio.Length == 32000 && audio.Any(b => b != 0), "Missing test tone");
        }
        if (scenario == "media-exit")
        {
            await Task.Delay(150, ct);
            Check(!requests.Any(r => r.StartsWith("delete-")), "Media exit tore down the call");
        }
        if (scenario == "shutdown") await service.StopAsync(ct);
        else if (scenario != "setup-failure")
        {
            await events.Writer.WriteAsync(Event("StasisEnd", "caller", "PJSIP/test"), ct);
            await events.Writer.WriteAsync(Event("ChannelDestroyed", "caller", "PJSIP/test"), ct);
        }
        await deleted.Task.WaitAsync(ct);
        Check(requests.Count(r => r.StartsWith("answer:")) == 1, "Duplicate or recursive call setup");
        Check(requests.Count(r => r.StartsWith("media:")) == 1, "Duplicate media channel");
        Check(requests.Contains("delete-channel:" + mediaId), "Media channel leaked");
        Check(requests.Contains("delete-bridge:" + bridgeId), "Bridge leaked");
        Check(requests.Contains("add:caller"), "Caller missing from bridge");
        if (scenario is not "setup-failure" and not "early-hangup") Check(requests.Contains("add:" + mediaId), "Media missing from bridge");
    }
    finally
    {
        await service.StopAsync(CancellationToken.None);
        await deadline.CancelAsync();
        await server.StopAsync(CancellationToken.None);
        File.Delete(audioPath);
    }
}

static string Event(string type, string id, string name) => JsonSerializer.Serialize(new { type, channel = new { id, name } });
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeSpeech(Action<string> onSpeech) : ISpeechSynthesizer
{
    public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        onSpeech(text);
        return Task.FromResult(new byte[640]);
    }
}

sealed class FakeMedia : ICallMedia
{
    private readonly byte[] recorded = [8, 7, 6, 5];
    public byte[]? FileBytes { get; private set; }
    public bool Recorded { get; private set; }
    public bool EchoPlayed { get; private set; }
    public Task Completion => Task.CompletedTask;
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken = default)
    {
        if (Recorded && pcmAudio.Span.SequenceEqual(recorded)) EchoPlayed = true;
        return Task.CompletedTask;
    }
    public async Task SendAudioAsync(Stream pcmAudio, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await pcmAudio.CopyToAsync(buffer, cancellationToken);
        FileBytes = buffer.ToArray();
    }
    public Task<byte[]> RecordAudioAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration != TimeSpan.FromSeconds(5)) throw new Exception("Incorrect recording duration");
        Recorded = true;
        return Task.FromResult(recorded);
    }
}
