using System.Threading.Channels;

namespace VoiceApp.Services;

public sealed class CallInput
{
    private readonly Channel<char> digits = Channel.CreateBounded<char>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private volatile bool overflow;

    public void Add(string? digit)
    {
        if (digit?.Length == 1 && !digits.Writer.TryWrite(digit[0])) overflow = true;
    }

    public void Clear()
    {
        while (digits.Reader.TryRead(out _)) { }
        overflow = false;
    }

    public async Task<char?> ReadAsync(TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { return await digits.Reader.ReadAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    public async Task<string?> ReadTenDigitsAsync(TimeSpan timeout, CancellationToken token)
    {
        // One deadline for the entire entry; # is required even after the tenth digit.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var number = new System.Text.StringBuilder(10);
        var invalid = false;
        try
        {
            while (true)
            {
                var digit = await digits.Reader.ReadAsync(deadline.Token);
                if (digit == '#') return !invalid && !overflow && number.Length == 10 ? number.ToString() : null;
                if (digit is < '0' or > '9' || number.Length == 10) invalid = true;
                else number.Append(digit);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }
}
