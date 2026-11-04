using CustomerServiceApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Voip.Telephony;

var queue = NewQueue(DispatchMode.Automatic, 2);
using (var first = queue.Enqueue("first", "orders"))
using (var second = queue.Enqueue("second", "billing"))
using (var third = queue.Enqueue("third", "support"))
using (var fourth = queue.Enqueue("fourth", "orders"))
{
    Check((await first.Assigned).Destination.Id == "agent-1", "First agent not assigned");
    Check((await second.Assigned).Destination.Id == "agent-2", "Second agent not assigned");
    Check(queue.Waiting.Select(c => c.Id).SequenceEqual(new[] { "third", "fourth" }), "Not FIFO");
    try { queue.Enqueue("first", "orders"); throw new Exception("Duplicate accepted"); }
    catch (InvalidOperationException) { }
    third.Dispose();
    first.Dispose();
    Check((await fourth.Assigned).Destination.Id == "agent-1", "Cancelled head blocked next caller");
    Check(queue.Waiting.Length == 0, "Waiting caller leaked");
}
Check(queue.Agents.All(a => a.CallId is null), "Agent leaked");
var old = queue.Enqueue("reused-id", "orders"); old.Dispose();
using (var replacement = queue.Enqueue("reused-id", "orders"))
{
    old.Dispose();
    Check(queue.Agents.Any(a => a.CallId == "reused-id"), "Old ticket released replacement");
}
Console.WriteLine("PASS automatic FIFO, capacity, duplicate prevention, cancellation, idempotent release");

queue = NewQueue(DispatchMode.Manual, 2);
using (var ticket = queue.Enqueue("manual", "orders"))
{
    Check(!ticket.Assigned.IsCompleted, "Manual mode assigned unconditionally");
    var attempts = await Task.WhenAll(Task.Run(() => queue.TryAccept("agent-1", "manual")),
        Task.Run(() => queue.TryAccept("agent-2", "manual")));
    Check(attempts.Count(x => x) == 1, "Two agents accepted the same caller");
    var chosen = await ticket.Assigned;
    using var next = queue.Enqueue("next", "billing");
    Check(!queue.TryAccept(chosen.Destination.Id, "next"), "Busy agent accepted another caller");
}
using (var abandoned = queue.Enqueue("abandoned", "orders")) { }
Check(!queue.TryAccept("agent-1", "abandoned"), "Accepted cancelled call");
Check(queue.Waiting.Length == 0 && queue.Agents.All(a => a.CallId is null), "Manual queue leaked");
Console.WriteLine("PASS manual view/accept, concurrent reservation, busy and abandoned calls");

foreach (var scenario in new[] { "automatic", "manual", "hangup", "timeout", "music-failure", "closed", "closing" })
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var input = new CallInput();
    var spoken = new List<string>();
    var speech = new FakeSpeech(text =>
    {
        spoken.Add(text);
        if (text.StartsWith("What would")) input.Add("2");
        if (text.StartsWith("You selected")) input.Add("1");
    });
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CustomerService:Override"] = scenario == "closed" ? "Closed" : "Open",
        ["CustomerService:MusicFile"] = Path.GetTempFileName(),
        ["CustomerService:MinimumHoldSeconds"] = "0.03",
        ["CustomerService:MaxWaitSeconds"] = scenario == "timeout" ? "0.1" : "5"
    }).Build();
    // Values are fractional seconds for fast, deterministic lifecycle tests.
    var file = config["CustomerService:MusicFile"]!;
    await File.WriteAllBytesAsync(file, new byte[640]);
    queue = NewQueue(scenario == "automatic" ? DispatchMode.Automatic : DispatchMode.Manual, 1);
    var media = new FakeMedia(scenario == "music-failure");
    var availability = new ServiceAvailability(config, TimeProvider.System);
    var flow = new CustomerServiceFlow(speech, new KeypadHelpTopicSelector(speech), queue, availability, config,
        NullLogger<CustomerServiceFlow>.Instance);
    try
    {
        var call = flow.RunAsync(media, input, deadline.Token);
        if (scenario is "manual" or "hangup" or "closing")
        {
            while (queue.Waiting.Length == 0) await Task.Delay(5, deadline.Token);
            Check(queue.Waiting.Single().Topic == "billing", "Topic lost when queued");
            if (scenario == "manual") Check(queue.TryAccept("agent-1", queue.Waiting.Single().Id), "Manual acceptance failed");
            else if (scenario == "closing")
            {
                availability.SetOverride(ServiceOverride.Closed);
                queue.SetAccepting(availability.IsOpen);
            }
            else deadline.Cancel();
        }
        try
        {
            await call;
            Check(scenario is not "hangup" and not "music-failure", "Expected failure did not propagate");
        }
        catch (OperationCanceledException) when (scenario == "hangup") { }
        catch (IOException) when (scenario == "music-failure") { }
        Check(queue.Waiting.Length == 0 && queue.Agents.All(a => a.CallId is null), "Flow leaked queue membership/agent");
        if (scenario is "automatic" or "manual")
        {
            Check(media.MusicStarted && media.MusicStopped, "Waiting music did not stop");
            Check(spoken.Last().Contains("demo response"), "Missing placeholder agent response");
        }
        if (scenario == "timeout") Check(spoken.Last().Contains("still busy"), "No timeout announcement");
        if (scenario is "closed" or "closing")
            Check(spoken.Last().Contains("currently closed") && spoken.Last().Contains("09:00"), "Missing closed message/hours");
        if (scenario == "closed") Check(!media.MusicStarted, "Closed service started music");
        Console.WriteLine("PASS customer-service flow: " + scenario);
    }
    finally { File.Delete(file); }
}

var hours = new ConfigurationBuilder().Build();
foreach (var (utc, expected) in new[]
{
    ("2026-11-04T07:59:00Z", false), ("2026-11-04T08:00:00Z", true),
    ("2026-11-04T15:59:00Z", true), ("2026-11-04T16:00:00Z", false),
    ("2026-11-07T10:00:00Z", false), ("2026-07-06T07:00:00Z", true)
})
{
    var availability = new ServiceAvailability(hours, new FixedTime(DateTimeOffset.Parse(utc)));
    Check(availability.IsOpen == expected, "Schedule/timezone boundary failed: " + utc);
    availability.SetOverride(ServiceOverride.Closed);
    Check(!availability.IsOpen, "Manual close ignored");
    availability.SetOverride(ServiceOverride.Open);
    Check(availability.IsOpen, "Manual open ignored");
    availability.SetOverride(ServiceOverride.Schedule);
    Check(availability.IsOpen == expected, "Schedule reset failed");
}
Console.WriteLine("PASS opening boundaries, weekends, daylight-saving timezone and overrides");

queue = NewQueue(DispatchMode.Manual, 1);
using (var accepted = queue.Enqueue("accepted", "orders"))
using (var waiting = queue.Enqueue("waiting", "orders"))
{
    queue.TryAccept("agent-1", "accepted");
    queue.SetAccepting(false);
    try { await waiting.Assigned; throw new Exception("Waiting caller not notified"); }
    catch (ServiceClosedException) { }
    Check(queue.Agents.Single().CallId == "accepted", "Closing interrupted accepted call");
    Check(!queue.TryAccept("agent-1", "waiting"), "Closed queue accepted call");
}
queue.SetAccepting(true);
using (var reopened = queue.Enqueue("reopened", "orders"))
    Check(queue.TryAccept("agent-1", "reopened"), "Queue failed to reopen");
Console.WriteLine("PASS closing waiting calls, preserving accepted calls, reopening");

static CallQueueManager NewQueue(DispatchMode mode, int count) => new(
    Enumerable.Range(1, count).Select(i => new CallDestination("agent-" + i, "200" + i)), mode);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FakeSpeech(Action<string> onSpeech) : ISpeechSynthesizer
{
    public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        onSpeech(text);
        return Task.FromResult(new byte[640]);
    }
}
sealed class FakeMedia(bool fail) : ICallMedia
{
    public bool MusicStarted { get; private set; }
    public bool MusicStopped { get; private set; }
    public Task Completion => Task.CompletedTask;
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task SendAudioAsync(Stream pcmAudio, CancellationToken cancellationToken = default)
    {
        MusicStarted = true;
        if (fail) throw new IOException("Simulated media failure");
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        finally { MusicStopped = true; }
    }
    public Task<byte[]> RecordAudioAsync(TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
