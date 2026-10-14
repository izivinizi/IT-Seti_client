namespace ITSeti.Maintenance.Core;

public static class HwInfoRecoveryPolicy
{
    public const int MissingReadsBeforeRestart = 6;
    public const int StaleSourceReadsBeforeRestart = 6;
    public static readonly TimeSpan MaximumSourceAge = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinimumRestartInterval = TimeSpan.FromMinutes(5);
    public static bool ShouldRestart(int consecutiveMissingReads, DateTimeOffset now, DateTimeOffset nextAllowedUtc) =>
        consecutiveMissingReads >= MissingReadsBeforeRestart && now >= nextAllowedUtc;

    public static bool IsSourceTimestampStale(DateTimeOffset sourceTimestampUtc, DateTimeOffset nowUtc) =>
        sourceTimestampUtc == DateTimeOffset.MinValue ||
        sourceTimestampUtc < nowUtc - MaximumSourceAge ||
        sourceTimestampUtc > nowUtc + MaximumFutureSkew;

    public static bool ShouldRestartStaleSource(int consecutiveStaleReads, DateTimeOffset now, DateTimeOffset nextAllowedUtc) =>
        consecutiveStaleReads >= StaleSourceReadsBeforeRestart && now >= nextAllowedUtc;
}
