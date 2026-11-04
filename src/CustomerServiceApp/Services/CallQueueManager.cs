namespace CustomerServiceApp.Services;

// Queue membership and assignment are separate. Both dispatch modes use the same
// atomic reservation, so two agents cannot accept the same call (or vice versa).
public sealed class CallQueueManager
{
    private readonly object sync = new();
    private readonly LinkedList<Ticket> waiting = new();
    private readonly Dictionary<string, Ticket> calls = new();
    private readonly Dictionary<string, (CallDestination Destination, Ticket? Call)> agents;
    private bool accepting = true;
    public DispatchMode Mode { get; }

    public CallQueueManager(IEnumerable<CallDestination> destinations, DispatchMode mode)
    {
        agents = destinations.ToDictionary(d => d.Id, d => (d, (Ticket?)null));
        if (agents.Count == 0 || agents.Values.Any(a => string.IsNullOrWhiteSpace(a.Destination.Id) || string.IsNullOrWhiteSpace(a.Destination.Number)))
            throw new ArgumentException("Configure at least one destination with a unique ID and number.");
        Mode = mode;
    }

    public Ticket Enqueue(string callId, string topic)
    {
        lock (sync)
        {
            if (!accepting) throw new ServiceClosedException();
            if (calls.ContainsKey(callId)) throw new InvalidOperationException("Call is already queued or assigned.");
            var ticket = new Ticket(this, new WaitingCall(callId, topic, DateTimeOffset.UtcNow));
            calls.Add(callId, ticket);
            ticket.Node = waiting.AddLast(ticket);
            Dispatch();
            return ticket;
        }
    }

    public bool TryAccept(string agentId, string callId)
    {
        lock (sync)
        {
            if (!accepting || !agents.TryGetValue(agentId, out var agent) || agent.Call is not null ||
                !calls.TryGetValue(callId, out var ticket) || ticket.Node is null) return false;
            Assign(ticket, agent.Destination);
            return true;
        }
    }

    public void RefreshAvailability(Func<bool> isOpen)
    {
        // Evaluate under the queue lock so a delayed schedule refresh cannot
        // overwrite a newer manual closure with a previously-read open value.
        lock (sync) SetAccepting(isOpen());
    }

    public void SetAccepting(bool value)
    {
        lock (sync)
        {
            accepting = value;
            if (value) { Dispatch(); return; }
            // Accepted calls finish normally. Only waiting callers are sent the closed message.
            while (waiting.First is { } node)
            {
                waiting.RemoveFirst();
                var ticket = node.Value;
                ticket.Node = null;
                calls.Remove(ticket.Call.Id);
                ticket.Source.TrySetException(new ServiceClosedException());
            }
        }
    }

    public WaitingCall[] Waiting { get { lock (sync) return waiting.Select(t => t.Call).ToArray(); } }
    public AgentState[] Agents
    {
        get { lock (sync) return agents.Values.Select(a => new AgentState(a.Destination.Id, a.Destination.Number, a.Call?.Call.Id)).ToArray(); }
    }

    private void Dispatch()
    {
        if (!accepting || Mode != DispatchMode.Automatic) return;
        foreach (var agent in agents.Values.ToArray())
        {
            if (waiting.First is null) break;
            if (agent.Call is null) Assign(waiting.First.Value, agent.Destination);
        }
    }

    private void Assign(Ticket ticket, CallDestination destination)
    {
        waiting.Remove(ticket.Node!);
        ticket.Node = null;
        ticket.Destination = destination;
        agents[destination.Id] = (destination, ticket);
        ticket.Source.SetResult(new CallAssignment(ticket.Call.Id, destination));
    }

    private void Release(Ticket ticket)
    {
        lock (sync)
        {
            if (!calls.TryGetValue(ticket.Call.Id, out var current) || !ReferenceEquals(current, ticket)) return;
            calls.Remove(ticket.Call.Id);
            if (ticket.Node is not null) waiting.Remove(ticket.Node);
            ticket.Node = null;
            if (ticket.Destination is not null)
                agents[ticket.Destination.Id] = (ticket.Destination, null);
            ticket.Source.TrySetCanceled();
            Dispatch();
        }
    }

    public sealed class Ticket : IDisposable
    {
        private readonly CallQueueManager owner;
        internal Ticket(CallQueueManager owner, WaitingCall call) { this.owner = owner; Call = call; }
        internal LinkedListNode<Ticket>? Node { get; set; }
        internal CallDestination? Destination { get; set; }
        internal TaskCompletionSource<CallAssignment> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WaitingCall Call { get; }
        public Task<CallAssignment> Assigned => Source.Task;
        public void Dispose() => owner.Release(this);
    }
}
