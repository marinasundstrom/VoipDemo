namespace VoiceApp.Services;

public sealed class CallSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private bool disposed;

    public CallInput Input { get; } = new();
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public string? BridgeId { get; set; }
    public string? MediaChannelId { get; set; }
    public AsteriskMediaSession? Media { get; set; }
    public TaskCompletionSource MediaEnteredStasis { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion { get; set; } = Task.CompletedTask;
    public CancellationToken CancellationToken => lifetime.Token;

    public void Cancel()
    {
        lock (sync)
        {
            if (!disposed)
                lifetime.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Media is not null)
            await Media.DisposeAsync();
        lock (sync)
        {
            disposed = true;
            lifetime.Dispose();
        }
    }
}
