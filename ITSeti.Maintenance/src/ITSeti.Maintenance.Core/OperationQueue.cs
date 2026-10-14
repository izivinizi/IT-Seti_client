namespace ITSeti.Maintenance.Core;

public enum OperationState { Waiting, Running, Retry, Cancelling, Cancelled, Completed, Failed }
public sealed record OperationStatus<T>(T Operation, string Group, OperationState State, int Attempts, string? Error, DateTimeOffset UpdatedAtUtc) where T : notnull;

/// <summary>Bounded by operation kinds, with duplicate coalescing and exclusive resource groups.</summary>
public sealed class OperationQueue<T> where T : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<T, OperationStatus<T>> entries = new();
    private readonly List<T> order = new();

    public bool Enqueue(T operation, string group, bool retry = false)
    {
        lock (gate)
        {
            if (entries.TryGetValue(operation, out var previous) &&
                previous.State is OperationState.Waiting or OperationState.Running or OperationState.Retry or OperationState.Cancelling) return false;
            entries[operation] = new(operation, group, retry ? OperationState.Retry : OperationState.Waiting,
                retry ? previous?.Attempts ?? 0 : 0, null, DateTimeOffset.UtcNow);
            order.Remove(operation);
            order.Add(operation);
            return true;
        }
    }

    public bool TryStart(T operation)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(operation, out var job) || job.State is not (OperationState.Waiting or OperationState.Retry)) return false;
            if (entries.Values.Any(other => other.Group == job.Group && other.State is OperationState.Running or OperationState.Cancelling)) return false;
            entries[operation] = job with { State = OperationState.Running, Attempts = job.Attempts + 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
            order.Remove(operation);
            return true;
        }
    }

    public void Complete(T operation, string? error = null)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(operation, out var job) || job.State is not (OperationState.Running or OperationState.Cancelling)) return;
            entries[operation] = job with { State = job.State == OperationState.Cancelling ? OperationState.Cancelled : error is null ? OperationState.Completed : OperationState.Failed,
                Error = error, UpdatedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    public bool Cancel(T operation)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(operation, out var job) || job.State is OperationState.Cancelled or OperationState.Completed or OperationState.Failed) return false;
            entries[operation] = job with { State = job.State is OperationState.Running or OperationState.Cancelling ? OperationState.Cancelling : OperationState.Cancelled, UpdatedAtUtc = DateTimeOffset.UtcNow };
            order.Remove(operation);
            return true;
        }
    }

    public IReadOnlyList<T> Pending { get { lock (gate) return order.ToArray(); } }
    public IReadOnlyList<OperationStatus<T>> Snapshot { get { lock (gate) return entries.Values.ToArray(); } }
}
