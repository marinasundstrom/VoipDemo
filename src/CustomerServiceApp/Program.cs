using CustomerServiceApp.Services;
using Voip.Telephony;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAsteriskTelephony<CustomerServiceFlow>();
builder.Services.AddSingleton<ISpeechSynthesizer, MacSpeechSynthesizer>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServiceAvailability>();
builder.Services.AddHostedService<OpeningHoursService>();
builder.Services.AddSingleton<IHelpTopicSelector, KeypadHelpTopicSelector>();
builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var destinations = config.GetSection("CustomerService:Destinations").Get<CallDestination[]>() ?? [];
    var mode = Enum.Parse<DispatchMode>(config["CustomerService:DispatchMode"] ?? "Automatic", ignoreCase: true);
    if (!Enum.IsDefined(mode)) throw new InvalidOperationException("Unknown dispatch mode.");
    var min = config.GetValue("CustomerService:MinimumHoldSeconds", 5.0);
    var max = config.GetValue("CustomerService:MaxWaitSeconds", 120.0);
    if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max <= min)
        throw new InvalidOperationException("MaxWaitSeconds must exceed MinimumHoldSeconds, which must be nonnegative.");
    return new CallQueueManager(destinations, mode);
});
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/service", (ServiceAvailability service) => Results.Ok(new
{
    isOpen = service.IsOpen, mode = service.Override.ToString(), openingHours = service.OpeningHours
}));
app.MapPut("/service/{mode}", (string mode, ServiceAvailability service, CallQueueManager queue) =>
{
    if (!Enum.TryParse<ServiceOverride>(mode, true, out var value) || !Enum.IsDefined(value))
        return Results.BadRequest(new { error = "Use Schedule, Open, or Closed." });
    service.SetOverride(value);
    queue.RefreshAvailability(() => service.IsOpen);
    return Results.Ok(new { isOpen = service.IsOpen, mode = service.Override.ToString(), openingHours = service.OpeningHours });
});
app.MapGet("/queue", (CallQueueManager queue) => Results.Ok(new { mode = queue.Mode.ToString(), waiting = queue.Waiting }));
app.MapGet("/agents", (CallQueueManager queue) => Results.Ok(queue.Agents));
app.MapPost("/agents/{agentId}/accept/{callId}", (string agentId, string callId, CallQueueManager queue, ServiceAvailability service) =>
{
    queue.RefreshAvailability(() => service.IsOpen);
    return queue.TryAccept(agentId, callId) ? Results.Ok() : Results.Conflict(new { error = "Service is closed, agent is busy/unknown, or call is no longer waiting." });
});
app.Run();
