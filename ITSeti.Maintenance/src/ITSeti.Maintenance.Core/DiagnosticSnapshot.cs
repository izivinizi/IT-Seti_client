namespace ITSeti.Maintenance.Core;

public sealed record DiskSnapshot(string Name, long TotalBytes, long FreeBytes, string VolumeId = "")
{
    public double UsedPercent => TotalBytes > 0 ? 100.0 * (TotalBytes - FreeBytes) / TotalBytes : 0;
    public string Capacity => $"{FreeBytes / 1073741824.0:N1} из {TotalBytes / 1073741824.0:N1} ГБ свободно";
    public string Usage => $"{UsedPercent:N0}%";
    public string Status => FreeBytes < 5L * 1073741824 ? "Почти нет свободного места"
        : FreeBytes < 15L * 1073741824 ? "Мало свободного места" : "Достаточно места";
}

public sealed record DiagnosticSnapshot(
    Guid Id, DateTimeOffset StartedAt, string ComputerName, double CpuPercent,
    ulong TotalMemoryBytes, ulong AvailableMemoryBytes, List<DiskSnapshot> Disks, List<string> Notes,
    FullDiagnosticDetails? Full = null, List<PhysicalDiskDetails>? QuickDisks = null,
    DateTimeOffset? LastBootAt = null, string? WindowsEdition = null, string? WindowsRelease = null,
    int? WindowsBuild = null, double? CpuTemperatureC = null, string? CpuTemperatureStatus = null)
{
    public double MemoryUsedPercent => TotalMemoryBytes > 0
        ? 100.0 * (TotalMemoryBytes - AvailableMemoryBytes) / TotalMemoryBytes : 0;
    public int AlertCount => DiagnosticRules.GetFindings(this).Count;
    public string DateLabel => StartedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
    public string CpuLabel => CpuPercent < 0 ? "Нет данных" : $"{CpuPercent:N0}%";
    public string MemoryLabel => TotalMemoryBytes == 0 ? "Нет данных" : $"{MemoryUsedPercent:N0}%";
    public string KindLabel => Full is null ? "Быстрая" : "Полная";
    public string ResultLabel
    {
        get
        {
            var incomplete = Full is { } full && (full.Benchmark.State != "Completed" || full.SmartDisks.Count == 0);
            var result = AlertCount > 0 ? $"Требует внимания: {AlertCount}" : "Замеренные показатели в норме";
            return incomplete ? result + " · неполные данные" : result;
        }
    }

    public string HistoryDetailLabel
    {
        get
        {
            var findings = DiagnosticRules.GetFindings(this)
                .Select(CondenseHistoryFact)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var details = findings.Take(2).ToList();
            if (Full is { } full)
            {
                details.AddRange(DiagnosticRules.GetActionableEvents(full.Events).Where(item => item.Level <= 2).OrderBy(item => item.Level)
                    .Take(Math.Max(0, 3 - details.Count)).Select(item => $"Ошибка Windows: {item.Provider} #{item.Id}"));
            }
            if (details.Count < 3) details.AddRange(findings.Skip(details.Count).Take(3 - details.Count));

            if (details.Count == 0) return ResultLabel;
            var result = string.Join(" · ", details.Distinct(StringComparer.CurrentCultureIgnoreCase));
            return result.Length <= 260 ? result : result[..257] + "...";
        }
    }

    private static string CondenseHistoryFact(string finding)
    {
        var sentenceEnd = finding.IndexOf(". ", StringComparison.Ordinal);
        return sentenceEnd < 0 ? finding : finding[..(sentenceEnd + 1)];
    }
}

public sealed record PhysicalDiskDetails(string Model, string MediaType, string Health);
public sealed record SmartDiskDetails(string Model, string Status, string Letters, string MediaType, string TransferMode, long? PowerOnHours = null);
public static class DiskLifetime
{
    public const long WarningThresholdHours = 60_000;

    public static bool ExceedsWarning(long? hours) => hours is > WarningThresholdHours;

    public static string Format(long? hours)
    {
        if (hours is not long value || value < 0) return "";
        var daysTotal = value / 24;
        return $"{value:N0} ч (около {daysTotal / 365} г. {daysTotal % 365} дн.)";
    }
}
public sealed record ProcessDetails(string Name, string Description, string Publisher, string Signature, string Path);
public sealed record MemoryProcessDetails(string DisplayName, string ProcessName, long WorkingSetBytes, string Publisher)
{
    public string MemoryLabel => $"{WorkingSetBytes / 1073741824.0:N1} ГБ";
}
public sealed record EventDetails(string Log, string Provider, int Id, int Level, string Time, string Message, long RecordId = 0)
{
    public string LevelLabel => Level switch { 1 => "Критическая", 2 => "Ошибка", 3 => "Предупреждение", _ => "Событие" };
}
public sealed record DiskBenchmark(string Drive, string MediaType, double? Read, double? Write, int Passes, string State, string Error, string Engine = "CrystalDiskMark", bool IsNvme = false)
{
    public string ReadLabel => Read.HasValue ? $"{Read:N1} МБ/с" : "Нет результата";
    public string WriteLabel => Write.HasValue ? $"{Write:N1} МБ/с" : "Нет результата";
}
public sealed record ResourceSampleSummary(int Samples, int CpuHighSamples, int MemoryHighSamples,
    int LowAvailableSamples, int PagingHighSamples, double CpuAverage, double MemoryAverage,
    double PagesOutputAverage, double? DiskReadLatencyMs, double? DiskWriteLatencyMs,
    long DiskReadOperations, long DiskWriteOperations, string Error);
public sealed record FullDiagnosticDetails(string CpuName, string GpuName, bool Elevated,
    List<PhysicalDiskDetails> PhysicalDisks, List<SmartDiskDetails> SmartDisks,
    List<ProcessDetails> Processes, List<EventDetails> Events, int ProcessUnavailable, int EventUnavailable,
    bool EventLimited, DiskBenchmark Benchmark, string ReportDirectory, List<MemoryProcessDetails>? TopMemoryProcesses = null,
    ResourceSampleSummary? ResourceSampling = null, string? MemoryType = null, double? CpuTemperatureC = null);

public sealed record DiagnosticProgress(string Stage, DiagnosticSnapshot? Snapshot = null, IReadOnlyList<string>? Messages = null);
public interface IFullDiagnosticsRunner
{
    Task<DiagnosticSnapshot> RunAsync(IProgress<DiagnosticProgress> progress);
    Task<DiagnosticSnapshot> RunUserAsync(IProgress<DiagnosticProgress> progress);
    Task<DiagnosticSnapshot> RunQuickAsync(IProgress<DiagnosticProgress> progress);
}

public interface IDiagnosticsRunner
{
    Task<DiagnosticSnapshot> RunAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task SaveAsync(DiagnosticSnapshot snapshot);
    Task SaveManyAsync(IReadOnlyCollection<DiagnosticSnapshot> snapshots);
    Task<IReadOnlyList<DiagnosticSnapshot>> GetRecentAsync(int limit = 100);
}
