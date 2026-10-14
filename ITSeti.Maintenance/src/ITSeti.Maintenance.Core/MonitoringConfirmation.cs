namespace ITSeti.Maintenance.Core;

/// <summary>Confirms fresh monitoring samples and repeated short events by issue type.</summary>
public sealed class MonitoringConfirmation
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumSampleGap = ConfirmationWindow;
    private static readonly TimeSpan RecurrenceWindow = TimeSpan.FromHours(1);
    private readonly Dictionary<string, Observation> observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> recentOccurrences = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset lastObservationAtUtc;

    private sealed record Observation(CriticalMonitoringIssue Issue, DateTimeOffset FirstAtUtc,
        DateTimeOffset LastSampleAtUtc, DateTimeOffset[] SampleTimes, bool Reported);

    private sealed record Policy(int RequiredSamples, TimeSpan RequiredDuration,
        int RequiredOccurrences = 0, TimeSpan MinimumOccurrenceDuration = default);

    public IReadOnlyList<CriticalMonitoringIssue> Observe(
        IReadOnlyList<CriticalMonitoringIssue> current, DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc == default)
        {
            observations.Clear();
            recentOccurrences.Clear();
            lastObservationAtUtc = default;
            return Array.Empty<CriticalMonitoringIssue>();
        }

        var samples = current
            .Select(issue => (Issue: issue, AtUtc: issue.ObservedAtUtc ?? observedAtUtc))
            .Where(sample => sample.AtUtc != default)
            .ToArray();
        var active = new HashSet<string>(samples.Select(sample => sample.Issue.Identity), StringComparer.OrdinalIgnoreCase);
        foreach (var key in observations.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            FinishEpisode(key, observations[key]);
            observations.Remove(key);
        }

        PruneOccurrences(observedAtUtc);
        var ready = new List<CriticalMonitoringIssue>();
        foreach (var (issue, sampleAtUtc) in samples)
        {
            var rule = GetPolicy(issue.Type);
            if (!observations.TryGetValue(issue.Identity, out var prior))
            {
                // The monitor samples every five seconds. If the preceding tick was healthy,
                // include that interval when measuring a newly observed condition.
                var firstAtUtc = lastObservationAtUtc != default &&
                    observedAtUtc > lastObservationAtUtc && observedAtUtc - lastObservationAtUtc <= MaximumSampleGap &&
                    lastObservationAtUtc < sampleAtUtc
                        ? lastObservationAtUtc : sampleAtUtc;
                var currentObservation = new Observation(issue, firstAtUtc, sampleAtUtc, [sampleAtUtc], false);
                var confirmedNewObservation = IsConfirmed(issue, currentObservation, rule);
                observations[issue.Identity] = currentObservation with { Reported = confirmedNewObservation };
                if (confirmedNewObservation) ready.Add(issue);
                continue;
            }

            // A cached HWiNFO frame is one reading, no matter how many service ticks see it.
            if (sampleAtUtc <= prior.LastSampleAtUtc) continue;

            var gap = sampleAtUtc - prior.LastSampleAtUtc;
            if (gap > MaximumSampleGap)
            {
                FinishEpisode(issue.Identity, prior);
                var currentObservation = new Observation(issue, sampleAtUtc, sampleAtUtc, [sampleAtUtc], false);
                var confirmedAfterGap = IsConfirmed(issue, currentObservation, rule);
                observations[issue.Identity] = currentObservation with { Reported = confirmedAfterGap };
                if (confirmedAfterGap) ready.Add(issue);
                continue;
            }

            var sampleTimes = prior.SampleTimes
                .Where(at => sampleAtUtc - at <= ConfirmationWindow)
                .Append(sampleAtUtc)
                .ToArray();
            var updated = new Observation(issue, prior.FirstAtUtc, sampleAtUtc, sampleTimes, prior.Reported);
            var confirmed = IsConfirmed(issue, updated, rule);
            observations[issue.Identity] = updated with { Reported = prior.Reported || confirmed };
            if (confirmed && !prior.Reported) ready.Add(issue);
        }

        lastObservationAtUtc = observedAtUtc;
        return ready;
    }

    private bool IsConfirmed(CriticalMonitoringIssue issue, Observation observation, Policy rule)
    {
        var duration = observation.LastSampleAtUtc - observation.FirstAtUtc;
        var sustained = observation.SampleTimes.Length >= rule.RequiredSamples && duration >= rule.RequiredDuration;
        if (sustained) return true;
        if (rule.RequiredOccurrences <= 0 || duration < rule.MinimumOccurrenceDuration) return false;
        var previous = recentOccurrences.TryGetValue(issue.Identity, out var events) ? events.Count : 0;
        return previous + 1 >= rule.RequiredOccurrences;
    }

    private void FinishEpisode(string identity, Observation observation)
    {
        var rule = GetPolicy(observation.Issue.Type);
        if (rule.RequiredOccurrences <= 0 ||
            observation.LastSampleAtUtc - observation.FirstAtUtc < rule.MinimumOccurrenceDuration) return;
        if (!recentOccurrences.TryGetValue(identity, out var events)) recentOccurrences[identity] = events = [];
        events.Add(observation.LastSampleAtUtc);
    }

    private void PruneOccurrences(DateTimeOffset now)
    {
        foreach (var entry in recentOccurrences.ToArray())
        {
            var identity = entry.Key;
            var events = entry.Value;
            events.RemoveAll(at => at > now || now - at > RecurrenceWindow);
            if (events.Count == 0) recentOccurrences.Remove(identity);
        }
    }

    private static Policy GetPolicy(MonitoringIssueType type) => type switch
    {
        MonitoringIssueType.Voltage => new(3, TimeSpan.Zero, 3),
        MonitoringIssueType.Temperature => new(6, TimeSpan.Zero, 2, TimeSpan.FromSeconds(15)),
        MonitoringIssueType.ResourceLoad => new(8, TimeSpan.Zero),
        MonitoringIssueType.OtherHwInfoCritical => new(2, TimeSpan.Zero),
        _ => new(6, TimeSpan.FromSeconds(30))
    };
}
