namespace ITSeti.Maintenance.Core;

public sealed record MonitoringOccurrence(CriticalMonitoringIssue Issue, DateTimeOffset FirstAtUtc,
    DateTimeOffset LastAtUtc, int Count);

public sealed record MonitoringBatchState(DateTimeOffset? LastSentAtUtc, IReadOnlyList<MonitoringOccurrence> Pending);

/// <summary>Persists confirmed episodes until the next hourly monitoring report.</summary>
public sealed class MonitoringReportBatch
{
    public static readonly TimeSpan MinimumReportInterval = TimeSpan.FromHours(1);
    private readonly Dictionary<string, MonitoringOccurrence> pending = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastSentAtUtc { get; private set; }

    public MonitoringReportBatch(MonitoringBatchState? state = null)
    {
        LastSentAtUtc = state?.LastSentAtUtc;
        foreach (var item in state?.Pending ?? []) pending[item.Issue.Identity] = item;
    }

    public void Add(IEnumerable<CriticalMonitoringIssue> issues, DateTimeOffset now)
    {
        foreach (var issue in issues.Where(issue => issue.Severity is "Critical" or "Warning"))
        {
            pending[issue.Identity] = pending.TryGetValue(issue.Identity, out var previous)
                ? previous with { Issue = issue, LastAtUtc = now, Count = previous.Count + 1 }
                : new(issue, now, now, 1);
        }
    }

    public IReadOnlyList<MonitoringOccurrence> Ready(DateTimeOffset now) => pending.Count > 0 &&
        (LastSentAtUtc is null || now - LastSentAtUtc.Value >= MinimumReportInterval)
            ? pending.Values.ToArray() : Array.Empty<MonitoringOccurrence>();

    public MonitoringBatchState Snapshot() => new(LastSentAtUtc, pending.Values.ToArray());
    public void MarkSent(DateTimeOffset now) { pending.Clear(); LastSentAtUtc = now; }
}
