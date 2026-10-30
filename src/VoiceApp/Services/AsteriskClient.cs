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
}