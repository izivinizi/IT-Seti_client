namespace ITSeti.Maintenance.Core;

// Explicit export for old web clients and PowerShell consumers. DTOs do not compute their own fields.
public static class DiagnosticReportProjection
{
    public static object? HwInfo(HwInfoSnapshot? snapshot) => snapshot is null ? null : new
    {
        snapshot.Source, snapshot.ToolVersion, snapshot.CapturedAtUtc, snapshot.Freshness,
        snapshot.CpuName, snapshot.Motherboard, snapshot.BiosVersion, snapshot.MemoryType, snapshot.Sensors,
        snapshot.CpuTemperatureC, snapshot.VrmTemperatureC, snapshot.PchTemperatureC,
        snapshot.Rail12V, snapshot.Rail5V, snapshot.Rail3V, snapshot.CpuVoltageV, snapshot.CpuLoadPercent, snapshot.MemoryLoadPercent
    };
}
