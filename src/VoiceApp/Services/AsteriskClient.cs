namespace VoiceApp.Services;

public sealed class AsteriskClient(HttpClient http)
{
    public async Task AnswerAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            $"channels/{Uri.EscapeDataString(channelId)}/answer",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task PlayAsync(
        string channelId,
        string sound,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            $"channels/{Uri.EscapeDataString(channelId)}/play" +
            $"?media={Uri.EscapeDataString($"sound:{sound}")}",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task HangupAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(
            $"channels/{Uri.EscapeDataString(channelId)}",
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task StartRecordingAsync(
    string channelId,
    string recordingName,
    CancellationToken cancellationToken = default)
    {
        var url =
            $"channels/{Uri.EscapeDataString(channelId)}/record" +
            $"?name={Uri.EscapeDataString(recordingName)}" +
            "&format=wav" +
            "&maxDurationSeconds=5" +
            "&ifExists=overwrite" +
            "&beep=false";

        using var response = await http.PostAsync(
            url,
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task PlayRecordingAsync(
        string channelId,
        string recordingName,
        CancellationToken cancellationToken = default)
    {
        var media = $"recording:{recordingName}";

        using var response = await http.PostAsync(
            $"channels/{Uri.EscapeDataString(channelId)}/play" +
            $"?media={Uri.EscapeDataString(media)}",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}