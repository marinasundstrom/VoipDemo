using System.Diagnostics;

namespace Voip.Telephony;

public sealed class MacSpeechSynthesizer(IConfiguration configuration) : ISpeechSynthesizer
{
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Live speech uses macOS say. Run the application locally on macOS or register another ISpeechSynthesizer.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            throw new ArgumentException("Speech text must contain 1–4000 characters.", nameof(text));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var folder = Directory.CreateTempSubdirectory("voiceapp-speech-");
        try
        {
            var input = Path.Combine(folder.FullName, "text.txt");
            var aiff = Path.Combine(folder.FullName, "speech.aiff");
            var pcm = Path.Combine(folder.FullName, "speech.pcm");
            await File.WriteAllTextAsync(input, text, timeout.Token);
            var args = new List<string> { "-f", input, "-o", aiff };
            var voice = configuration["Speech:Voice"];
            if (!string.IsNullOrWhiteSpace(voice)) args.AddRange(["-v", voice]);
            await RunAsync("/usr/bin/say", args, timeout.Token);
            await RunAsync(configuration["Speech:FfmpegPath"] ?? "ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-y", "-i", aiff,
                 "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", pcm], timeout.Token);
            return await File.ReadAllBytesAsync(pcm, timeout.Token);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {executable}.");
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            var details = await error;
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed: {details.Trim()}");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await error; } catch (OperationCanceledException) { }
            throw;
        }
    }
}
