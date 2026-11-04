using Voip.Telephony;

namespace CustomerServiceApp.Services;

public sealed class CustomerServiceFlow(
    ISpeechSynthesizer speech,
    IHelpTopicSelector topics,
    CallQueueManager queue,
    ServiceAvailability availability,
    IConfiguration configuration,
    ILogger<CustomerServiceFlow> logger) : ICallFlow
{
    public async Task RunAsync(ICallMedia media, CallInput input, CancellationToken token)
    {
        queue.RefreshAvailability(() => availability.IsOpen);
        if (!availability.IsOpen)
        {
            logger.LogInformation("Service closed: announcing opening hours");
            await SayAsync(media, availability.ClosedMessage, token);
            return;
        }
        await SayAsync(media, "Welcome to customer service.", token);
        var topic = await topics.SelectAsync(media, input, token);
        if (topic is null) { await SayAsync(media, "Goodbye.", token); return; }
        input.Clear();
        await SayAsync(media, $"You selected {topic}. Press 1 to speak with customer service, or 0 to finish.", token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var choice = await input.ReadAsync(TimeSpan.FromSeconds(15), token);
            if (choice == '1')
            {
                try
                {
                    queue.RefreshAvailability(() => availability.IsOpen);
                    await JoinQueueAsync(media, topic, token);
                }
                catch (ServiceClosedException) { await SayAsync(media, availability.ClosedMessage, token); }
                return;
            }
            if (choice == '0') { await SayAsync(media, "Goodbye.", token); return; }
            if (attempt < 2) await SayAsync(media, "Please press 1 for customer service, or 0 to finish.", token);
        }
        await SayAsync(media, "No valid option was received. Please call again. Goodbye.", token);
    }

    private async Task JoinQueueAsync(ICallMedia media, string topic, CancellationToken token)
    {
        await SayAsync(media, "You are being placed in the customer service queue. Please hold.", token);
        using var ticket = queue.Enqueue(Guid.NewGuid().ToString("N"), topic);
        // Hangup removes a waiting ticket or releases an assigned agent immediately.
        using var cancellation = token.Register(ticket.Dispose);
        logger.LogInformation("Queue ticket {CallId} created for {Topic} in {Mode} mode", ticket.Call.Id, topic, queue.Mode);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(configuration.GetValue("CustomerService:MaxWaitSeconds", 120.0)));
        using var musicLifetime = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
        var music = PlayWaitingMusicAsync(media, musicLifetime.Token);
        var assignment = ticket.Assigned.WaitAsync(wait.Token);
        var ready = Task.WhenAll(assignment,
            Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("CustomerService:MinimumHoldSeconds", 5.0)), wait.Token));
        try
        {
            // Propagate a media/file failure promptly instead of leaking a queue reservation.
            await await Task.WhenAny(ready, music);
            await ready;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && wait.IsCancellationRequested)
        {
            ticket.Dispose();
            await musicLifetime.CancelAsync();
            try { await music; } catch (OperationCanceledException) { }
            await SayAsync(media, "Our team is still busy. Please call again later. Goodbye.", token);
            return;
        }
        finally
        {
            await musicLifetime.CancelAsync();
            try { await music; } catch (OperationCanceledException) { }
        }
        token.ThrowIfCancellationRequested();
        var selected = await assignment;
        logger.LogInformation("Assigned queue call {CallId} to {AgentId} ({Number})", ticket.Call.Id, selected.Destination.Id, selected.Destination.Number);
        await SayAsync(media, $"Your call has been accepted by customer service at extension {selected.Destination.Number}. " +
            "This is the demo response. In the full service, you would now speak with an adviser. Thank you for calling. Goodbye.", token);
        // Returning closes the call through the shared ARI lifecycle; using releases the agent.
    }

    private async Task PlayWaitingMusicAsync(ICallMedia media, CancellationToken token)
    {
        var path = configuration["CustomerService:MusicFile"] ?? throw new InvalidOperationException("Configure CustomerService:MusicFile.");
        await using var audio = File.OpenRead(path);
        if (audio.Length == 0 || audio.Length % 2 != 0) throw new InvalidDataException("Waiting music must be nonempty s16le PCM.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            audio.Position = 0;
            await media.SendAudioAsync(audio, token);
        }
    }

    private async Task SayAsync(ICallMedia media, string text, CancellationToken token)
    {
        await media.SendAudioAsync(await speech.SynthesizeAsync(text, token), token);
        await Task.Delay(150, token);
    }
}
