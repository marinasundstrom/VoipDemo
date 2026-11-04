namespace CustomerServiceApp.Services;

public enum ServiceOverride { Schedule, Open, Closed }
public sealed class ServiceClosedException : Exception
{
    public ServiceClosedException() : base("Customer service is closed.") { }
}

public sealed class ServiceAvailability
{
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo zone;
    private readonly DayOfWeek[] days;
    private readonly TimeOnly opens;
    private readonly TimeOnly closes;
    private int mode;

    public ServiceAvailability(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["CustomerService:Hours:TimeZone"] ?? "Europe/Stockholm");
        days = (configuration.GetSection("CustomerService:Hours:Days").Get<string[]>() ??
            ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]).Select(Enum.Parse<DayOfWeek>).Distinct().ToArray();
        opens = TimeOnly.ParseExact(configuration["CustomerService:Hours:Open"] ?? "09:00", "HH:mm");
        closes = TimeOnly.ParseExact(configuration["CustomerService:Hours:Close"] ?? "17:00", "HH:mm");
        if (days.Length == 0 || days.Any(d => !Enum.IsDefined(d)) || opens >= closes)
            throw new ArgumentException("Configure weekdays and a daytime opening interval with Open before Close.");
        SetOverride(Enum.Parse<ServiceOverride>(configuration["CustomerService:Override"] ?? "Schedule", true));
    }

    public ServiceOverride Override => (ServiceOverride)Volatile.Read(ref mode);
    public bool IsOpen
    {
        get
        {
            var current = Override;
            if (current != ServiceOverride.Schedule) return current == ServiceOverride.Open;
            var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
            var time = TimeOnly.FromDateTime(now.DateTime);
            return days.Contains(now.DayOfWeek) && time >= opens && time < closes;
        }
    }
    public string OpeningHours => $"{string.Join(", ", days)}, from {opens:HH:mm} to {closes:HH:mm}, {zone.Id} time";
    public string ClosedMessage => $"Customer service is currently closed. Please call again during our opening hours: {OpeningHours}. Goodbye.";
    public void SetOverride(ServiceOverride value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Volatile.Write(ref mode, (int)value);
    }
}

// Re-evaluate scheduled closing even when no new calls or operator requests arrive.
public sealed class OpeningHoursService(ServiceAvailability availability, CallQueueManager queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do { queue.RefreshAvailability(() => availability.IsOpen); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
