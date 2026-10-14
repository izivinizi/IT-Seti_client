namespace ITSeti.Maintenance.Core;

public sealed record DiagnosticDataQuality(string Area, string Status, string Source, DateTimeOffset? ReadAtUtc, string Freshness);
public sealed record DiagnosticEvaluation(int SchemaVersion, string RulesVersion, IReadOnlyList<UserIssue> Issues,
    IReadOnlyList<string> Findings, IReadOnlyList<DiagnosticDataQuality> DataQuality);

public static class DiagnosticEngine
{
    public const int SchemaVersion = 1;
    public const string RulesVersion = "2.4.1";

    public static DiagnosticEvaluation Evaluate(DiagnosticSnapshot snapshot)
    {
        var hwInfo = snapshot.HwInfo;
        var age = hwInfo is null ? TimeSpan.MaxValue : snapshot.StartedAt.ToUniversalTime() - hwInfo.CapturedAtUtc;
        var status = hwInfo is null || hwInfo.Sensors.Count == 0 ? "Unavailable"
            : age > TimeSpan.FromSeconds(90) ? "Stale" : "Unverified";
        var quality = new List<DiagnosticDataQuality>
        {
            new("HWiNFO", status, hwInfo?.Source ?? "HWiNFO", hwInfo?.CapturedAtUtc, hwInfo?.Freshness ?? "Unavailable"),
            new("Memory", snapshot.TotalMemoryBytes > 0 ? "Available" : "Unavailable", "Windows/HWiNFO", snapshot.StartedAt, "ReadTime"),
            new("ResourceSampling", snapshot.Full?.ResourceSampling is { Samples: >= 6, Error: "" } ? "Available" : "Unavailable", "ResourceSampler", snapshot.StartedAt, "CheckInterval"),
            new("SMART", snapshot.Full?.SmartDisks.Count > 0 ? "Available" : "Unavailable", "CrystalDiskInfo", snapshot.StartedAt, "CheckInterval")
        };
        // Evaluate age relative to the report, so reopening historical reports is deterministic.
        var input = status == "Stale" ? snapshot with { HwInfo = null } : snapshot;
        return new(SchemaVersion, RulesVersion, DiagnosticRules.GetUserIssues(input),
            DiagnosticRules.GetFindings(input, includeDiskLinkWarnings: false), quality);
    }
}
