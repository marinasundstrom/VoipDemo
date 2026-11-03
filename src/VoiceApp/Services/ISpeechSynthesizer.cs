namespace VoiceApp.Services;

// Return raw 16 kHz, mono, signed 16-bit little-endian PCM.
// A future AI/TTS provider can implement this without changing call control.
public interface ISpeechSynthesizer
{
    Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}
