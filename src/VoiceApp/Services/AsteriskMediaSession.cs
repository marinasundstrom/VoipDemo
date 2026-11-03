using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceApp.Services;

public sealed class AsteriskMediaSession : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task receiveTask = Task.CompletedTask;
    private int frameSize;
    private readonly SemaphoreSlim sender = new(1, 1);
    private readonly object flowSync = new();
    private TaskCompletionSource? resume;
    private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    public async Task<byte[]> RecordAudioAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var length = checked((int)(duration.TotalSeconds * 16000) * 2);
        if (length <= 0 || length > 32000 * 60)
            throw new ArgumentOutOfRangeException(nameof(duration), "Record between zero and sixty seconds.");
        // Discard media received during the announcement/beep.
        while (incoming.Reader.TryRead(out _)) { }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration + TimeSpan.FromSeconds(3));
        var audio = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunk = await incoming.Reader.ReadAsync(timeout.Token);
            var count = Math.Min(chunk.Length, length - offset);
            chunk.AsSpan(0, count).CopyTo(audio.AsSpan(offset));
            offset += count;
        }
        return audio;
    }

    public Task Completion => receiveTask;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        socket.Options.AddSubProtocol("media");
        await socket.ConnectAsync(uri, cancellationToken);
        receiveTask = ReceiveAsync(lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcmAudio,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(pcmAudio.ToArray(), writable: false);
        await SendAudioAsync(stream, cancellationToken);
    }

    // Raw signed 16-bit little-endian PCM, mono, 16 kHz (no WAV/AIFF header).
    public async Task SendAudioAsync(Stream pcmAudio, CancellationToken cancellationToken = default)
    {
        await started.Task.WaitAsync(cancellationToken);
        await sender.WaitAsync(cancellationToken);
        try
        {
            var frame = new byte[frameSize];
            while (true)
            {
                var count = 0;
                while (count < frame.Length)
                {
                    var read = await pcmAudio.ReadAsync(frame.AsMemory(count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) break;
                if (count % 2 != 0) throw new InvalidDataException("PCM ends in a partial 16-bit sample.");
                frame.AsSpan(count).Clear(); // Pad the last frame with silence.
                Task ready;
                lock (flowSync) ready = resume?.Task ?? Task.CompletedTask;
                await ready.WaitAsync(cancellationToken);
                await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
                // Bound buffering even for long speech files; Asterisk provides final timing.
                await Task.Delay(TimeSpan.FromSeconds(frameSize / 32000.0), cancellationToken);
            }
        }
        finally { sender.Release(); }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    incoming.Writer.TryWrite(buffer.AsSpan(0, result.Count).ToArray());
                    continue;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > 65500)
                    throw new InvalidDataException("Media control message is too large.");
                if (!result.EndOfMessage)
                    continue;
                HandleControl(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
            started.TrySetException(new IOException("Media WebSocket closed before MEDIA_START."));
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            throw;
        }
        finally
        {
            incoming.Writer.TryComplete();
            lock (flowSync)
            {
                resume?.TrySetCanceled();
                resume = null;
            }
        }
    }

    private void HandleControl(string message)
    {
        string? eventName;
        string? format;
        int size;
        if (message.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            eventName = root.GetProperty("event").GetString();
            if (HandleFlowControl(eventName) || eventName != "MEDIA_START") return;
            format = root.GetProperty("format").GetString();
            size = root.GetProperty("optimal_frame_size").GetInt32();
        }
        else
        {
            var parts = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            eventName = parts.FirstOrDefault();
            if (HandleFlowControl(eventName) || eventName != "MEDIA_START") return;
            var fields = parts.Skip(1).Select(p => p.Split(':', 2))
                .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            format = fields.GetValueOrDefault("format");
            _ = int.TryParse(fields.GetValueOrDefault("optimal_frame_size"), out size);
        }
        if (format != "slin16" || size <= 0 || size > 65500 || size % 2 != 0)
            throw new InvalidDataException("Expected slin16 media with a valid PCM frame size.");
        frameSize = size;
        started.TrySetResult();
    }

    private bool HandleFlowControl(string? eventName)
    {
        lock (flowSync)
        {
            if (eventName == "MEDIA_XOFF")
                resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            else if (eventName == "MEDIA_XON")
            {
                resume?.TrySetResult();
                resume = null;
            }
        }
        return eventName is "MEDIA_XOFF" or "MEDIA_XON";
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        socket.Abort();
        try { await receiveTask; }
        catch (Exception) { /* The call task observes receive failures; disposal must still release the socket. */ }
        socket.Dispose();
        lifetime.Dispose();
    }
}
