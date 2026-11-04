namespace Voip.Telephony;

public interface ICallFlow
{
    Task RunAsync(ICallMedia media, CallInput input, CancellationToken token);
}
