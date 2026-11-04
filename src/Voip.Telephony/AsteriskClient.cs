using System.Net;
using System.Text.Json;

namespace Voip.Telephony;

public sealed class AsteriskClient(HttpClient http, IConfiguration? configuration = null)
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

        if (response.StatusCode != HttpStatusCode.NotFound)
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

    public async Task<byte[]> GetRecordingAsync(
    string recordingName,
    CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(
            $"recordings/stored/{Uri.EscapeDataString(recordingName)}/file",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(
            cancellationToken);
    }

    public async Task<string> CreateBridgeAsync(
        string bridgeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}?type=mixing",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        return json.RootElement.GetProperty("id").GetString()!;
    }

    public async Task AddChannelToBridgeAsync(
        string bridgeId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}/addChannel" +
            $"?channel={Uri.EscapeDataString(channelId)}",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<string> CreateExternalMediaChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string>
        {
            ["app"] = configuration?["Asterisk:Application"] ?? "voiceapp",
            ["channelId"] = channelId,
            ["external_host"] = "INCOMING",
            ["transport"] = "websocket",
            ["encapsulation"] = "none",
            ["connection_type"] = "server",
            ["format"] = "slin16"
        };

        var url = "channels/externalMedia?" +
            string.Join("&", query.Select(x =>
                $"{x.Key}={Uri.EscapeDataString(x.Value)}"));

        using var response = await http.PostAsync(
            url,
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        return json.RootElement
            .GetProperty("id")
            .GetString()!;
    }

    public async Task<string?> GetChannelVariableAsync(
        string channelId,
        string variable,
        CancellationToken cancellationToken = default)
    {
        var url =
            $"channels/{Uri.EscapeDataString(channelId)}/variable" +
            $"?variable={Uri.EscapeDataString(variable)}";

        using var response = await http.GetAsync(
            url,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        return json.RootElement
            .GetProperty("value")
            .GetString();
    }

    public async Task DestroyBridgeAsync(
        string bridgeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(
            $"bridges/{Uri.EscapeDataString(bridgeId)}", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public async Task StartMusicOnHoldAsync(
    string channelId,
    CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            $"channels/{Uri.EscapeDataString(channelId)}/moh",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task StopMusicOnHoldAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(
            $"channels/{Uri.EscapeDataString(channelId)}/moh",
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<string> OriginateAsync(
    string endpoint,
    string callId,
    CancellationToken cancellationToken = default)
    {
        var channelId = Guid.NewGuid().ToString("N");

        var url =
            $"channels/{Uri.EscapeDataString(channelId)}" +
            $"?endpoint={Uri.EscapeDataString(endpoint)}" +
            $"&app={Uri.EscapeDataString(configuration?["Asterisk:Application"] ?? "voiceapp")}" +
            $"&appArgs={Uri.EscapeDataString($"agent,{callId}")}" +
            "&timeout=30";

        using var response = await http.PostAsync(
            url,
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return channelId;
    }
}
