namespace Voip.Telephony;

public interface ICallMedia
{
    Task Completion { get; }
    Task SendAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken = default);
    Task SendAudioAsync(Stream pcmAudio, CancellationToken cancellationToken = default);
    Task<byte[]> RecordAudioAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
