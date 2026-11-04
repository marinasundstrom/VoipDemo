namespace CustomerServiceApp.Services;

public sealed record CallDestination(string Id, string Number);
public sealed record CallAssignment(string CallId, CallDestination Destination);
public sealed record WaitingCall(string Id, string Topic, DateTimeOffset QueuedAt);
public sealed record AgentState(string Id, string Number, string? CallId);
public enum DispatchMode { Automatic, Manual }
