using Voip.Telephony;
using System.Buffers.Binary;

namespace VoiceApp.Services;

public sealed class DemoCallFlow(
    IConfiguration configuration,
    ISpeechSynthesizer speech,
    ILogger<DemoCallFlow> logger) : ICallFlow
{
    public async Task RunAsync(ICallMedia media, CallInput input, CancellationToken token)
    {
        if (!string.Equals(configuration["Demo:Scenario"], "Menu", StringComparison.OrdinalIgnoreCase))
        {
            await PlayAnnouncementAsync(media, token);
            await EchoAsync(media, token);
            await media.Completion.WaitAsync(token);
            return;
        }

        while (true)
        {
            var choice = await ChooseAsync(media, input,
                "Welcome to the voice demo. Press 1 for recorded music. 2 for live speech. " +
                "3 to select an option. 4 to enter a ten digit number. 5 to record and hear your voice. " +
                "Or press 0 to finish.", "012345", token);
            if (choice is null or '0')
            {
                await SayAsync(media, "Goodbye.", token);
                return;
            }
            switch (choice)
            {
                case '1':
                    var path = configuration["Demo:RecordingFile"];
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        await SayAsync(media, "No music file is configured yet.", token);
                    else
                    {
                        await SayAsync(media, "Here is a short recording.", token);
                        await using var audio = File.OpenRead(path);
                        await media.SendAudioAsync(audio, token);
                    }
                    break;
                case '2':
                    // Construct text at call time, rather than replaying a generated prompt file.
                    var time = DateTimeOffset.Now;
                    await SayAsync(media,
                        $"This sentence was synthesized during your call. The server time is {time:HH:mm:ss}. " +
                        (configuration["Demo:LiveText"] ?? "A future AI service can provide the text for this same speech interface."), token);
                    break;
                case '3':
                    var selected = await ChooseAsync(media, input,
                        "Choose an option. Press 1 for sales. 2 for support. Or 3 for opening hours.", "123", token);
                    await SayAsync(media, selected switch
                    {
                        '1' => "You selected sales.",
                        '2' => "You selected support.",
                        '3' => "You selected opening hours.",
                        _ => "No option was selected."
                    }, token);
                    break;
                case '4':
                    await EnterNumberAsync(media, input, token);
                    break;
                case '5':
                    await SayAsync(media, "After the beep, say something. I will record for five seconds, then play it back.", token);
                    await EchoAsync(media, token);
                    break;
            }
        }
    }

    private async Task<char?> ChooseAsync(ICallMedia media, CallInput input, string prompt, string allowed, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            input.Clear(); // Accept input during this prompt, but not keys from the previous scenario.
            await SayAsync(media, prompt, token);
            var digit = await input.ReadAsync(TimeSpan.FromSeconds(15), token);
            if (digit.HasValue && allowed.Contains(digit.Value)) return digit;
            await SayAsync(media, digit.HasValue ? "That option is not available. Please try again." : "No input received. Please try again.", token);
        }
        return null;
    }

    private async Task EnterNumberAsync(ICallMedia media, CallInput input, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            input.Clear();
            await SayAsync(media, "Enter exactly ten digits, followed by the hash key.", token);
            var number = await input.ReadTenDigitsAsync(TimeSpan.FromSeconds(30), token);
            if (number is not null)
            {
                // Keep leading zeroes and speak each digit. Never log the entered number.
                await SayAsync(media, "You entered: " + string.Join(", ", number.ToCharArray()) + ".", token);
                return;
            }
            await SayAsync(media, "That was not ten digits followed by hash, or the entry timed out. Please try again.", token);
        }
    }

    private async Task SayAsync(ICallMedia media, string text, CancellationToken token)
    {
        var audio = await speech.SynthesizeAsync(text, token);
        await media.SendAudioAsync(audio, token);
        await Task.Delay(150, token);
    }

    private async Task PlayAnnouncementAsync(ICallMedia media, CancellationToken token)
    {
        var path = configuration["Asterisk:AudioFile"];
        if (string.IsNullOrWhiteSpace(path)) await media.SendAudioAsync(CreateTone(440, 1.0), token);
        else
        {
            await using var audio = File.OpenRead(path);
            await media.SendAudioAsync(audio, token);
        }
    }

    private async Task EchoAsync(ICallMedia media, CancellationToken token)
    {
        await Task.Delay(250, token);
        await media.SendAudioAsync(CreateTone(1000, 0.2), token);
        await Task.Delay(250, token);
        logger.LogInformation("Recording five seconds of caller audio");
        var recording = await media.RecordAudioAsync(TimeSpan.FromSeconds(5), token);
        logger.LogInformation("Playing back {Bytes} recorded PCM bytes", recording.Length);
        await media.SendAudioAsync(recording, token);
        logger.LogInformation("Echo sequence complete");
    }

    private static byte[] CreateTone(double frequency, double seconds)
    {
        const int sampleRate = 16000;
        var samples = (int)(sampleRate * seconds);
        var audio = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i, samples - 1 - i) / 160.0);
            var sample = (short)(6000 * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 2), sample);
        }
        return audio;
    }
}
