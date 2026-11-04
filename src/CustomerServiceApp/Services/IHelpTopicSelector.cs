using Voip.Telephony;

namespace CustomerServiceApp.Services;

// Replace the keypad implementation with speech/AI intent recognition later.
public interface IHelpTopicSelector
{
    Task<string?> SelectAsync(ICallMedia media, CallInput input, CancellationToken token);
}

public sealed class KeypadHelpTopicSelector(ISpeechSynthesizer speech) : IHelpTopicSelector
{
    public async Task<string?> SelectAsync(ICallMedia media, CallInput input, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            input.Clear();
            await media.SendAudioAsync(await speech.SynthesizeAsync(
                "What would you like help with? Press 1 for orders, 2 for billing, 3 for technical support, or 0 to finish.", token), token);
            var digit = await input.ReadAsync(TimeSpan.FromSeconds(15), token);
            switch (digit)
            {
                case '0': return null;
                case '1': return "orders";
                case '2': return "billing";
                case '3': return "technical support";
            }
            await media.SendAudioAsync(await speech.SynthesizeAsync("No valid choice was received. Please try again.", token), token);
        }
        return null;
    }
}
