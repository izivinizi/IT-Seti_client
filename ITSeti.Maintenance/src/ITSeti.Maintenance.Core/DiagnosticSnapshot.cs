namespace ITSeti.Maintenance.Core;

public sealed partial record DiskSnapshot(string Name, long TotalBytes, long FreeBytes, string VolumeId = "");

public sealed partial record DiagnosticSnapshot(
    Guid Id, DateTimeOffset StartedAt, string ComputerName, double CpuPercent,
    ulong TotalMemoryBytes, ulong AvailableMemoryBytes, List<DiskSnapshot> Disks, List<string> Notes,
    FullDiagnosticDetails? Full = null, List<PhysicalDiskDetails>? QuickDisks = null,
    DateTimeOffset? LastBootAt = null, string? WindowsEdition = null, string? WindowsRelease = null,
    int? WindowsBuild = null, double? CpuTemperatureC = null, string? CpuTemperatureStatus = null,
    double? ActiveUptimeHours = null, double? DiskReadLatencyMs = null, double? DiskWriteLatencyMs = null,
    HwInfoSnapshot? HwInfo = null, List<NetworkAdapterSnapshot>? NetworkAdapters = null, string SystemDrive = "C:",
    DateTimeOffset? CpuTemperatureCapturedAtUtc = null, DateTimeOffset? CpuPercentCapturedAtUtc = null,
    DateTimeOffset? MemoryUsedPercentCapturedAtUtc = null);

public sealed record PhysicalDiskDetails(string Model, string MediaType, string Health, long? PowerOnHours = null);
public sealed record NetworkAdapterSnapshot(string Name, string Type, string Status, string LinkSpeed,
    string MacAddress, string Addresses);
public sealed record SmartDiskDetails(string Model, string Status, string Letters, string MediaType, string TransferMode, long? PowerOnHours = null)
{
    public string SmartWarnings { get; init; } = "";
}
public sealed partial record ProcessDetails(string Name, string Description, string Publisher, string Signature, string Path);
public sealed partial record MemoryProcessDetails(string DisplayName, string ProcessName, long WorkingSetBytes, string Publisher);
public sealed partial record EventDetails(string Log, string Provider, int Id, int Level, string Time, string Message, long RecordId = 0);
public sealed partial record DiskBenchmark(string Drive, string MediaType, double? Read, double? Write, int Passes, string State, string Error, string Engine = "CrystalDiskMark", bool IsNvme = false);
public sealed record ResourceSampleSummary(int Samples, int CpuHighSamples, int MemoryHighSamples,
    int LowAvailableSamples, int PagingHighSamples, double CpuAverage, double MemoryAverage,
    double PagesOutputAverage, double? DiskReadLatencyMs, double? DiskWriteLatencyMs,
    long DiskReadOperations, long DiskWriteOperations, string Error,
    int CpuCriticalSamples = 0, int MemoryCriticalSamples = 0);
public sealed record HwInfoSensorReading(string Name, double Value, string Unit, string Category,
    string? Status = null, bool IsWarning = false, bool IsCritical = false,
    double? RawValue = null, string Quality = "Unverified", string? SensorId = null);
public sealed record SensorHistoryReading(string Name, double Value, string Unit, string Category);
public sealed record SensorHistorySample(DateTimeOffset CapturedAtUtc, double? CpuLoadPercent,
    double? MemoryLoadPercent, IReadOnlyList<SensorHistoryReading> Sensors, DateTimeOffset? HwInfoCapturedAtUtc = null);
public sealed partial record HwInfoSnapshot(string Source, string ToolVersion, DateTimeOffset CapturedAtUtc,
    string? CpuName, string? Motherboard, string? BiosVersion, string? MemoryType,
    IReadOnlyList<HwInfoSensorReading> Sensors,
    string Freshness = "SourceTimestampUnavailable");
public sealed record FullDiagnosticDetails(string CpuName, string GpuName, bool Elevated,
    List<PhysicalDiskDetails> PhysicalDisks, List<SmartDiskDetails> SmartDisks,
    List<ProcessDetails> Processes, List<EventDetails> Events, int ProcessUnavailable, int EventUnavailable,
    bool EventLimited, DiskBenchmark Benchmark, string ReportDirectory, List<MemoryProcessDetails>? TopMemoryProcesses = null,
    ResourceSampleSummary? ResourceSampling = null, string? MemoryType = null, double? CpuTemperatureC = null);


public sealed partial record UserIssue(string Title, string Detail, string Severity);
