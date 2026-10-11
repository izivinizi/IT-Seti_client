using System.Text.Json;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public static class MaintenanceServiceStatusReader
{
    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "Service", "status.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static DiagnosticSnapshot? ReadSnapshot(TimeSpan maximumAge)
    {
        try
        {
            if (!File.Exists(StatusPath)) return null;
            var status = JsonSerializer.Deserialize<Status>(File.ReadAllText(StatusPath), Options);
            if (status is null || status.CapturedAtUtc == default ||
                DateTimeOffset.UtcNow - status.CapturedAtUtc.ToUniversalTime() > maximumAge) return null;
            var total = status.TotalMemoryBytes ?? 0;
            var used = status.MemoryUsedPercent is { } memory ? Math.Clamp(memory / 100.0, 0, 1) : 0;
            var available = status.AvailableMemoryBytes ?? (ulong)Math.Round(total * (1 - used));
            var uptime = status.UptimeHours is { } hours
                ? DateTimeOffset.Now - TimeSpan.FromHours(Math.Max(0, hours)) : (DateTimeOffset?)null;
            var notes = status.CriticalIssues?.Select(issue => issue.Detail).ToList() ?? [];
            return new DiagnosticSnapshot(Guid.NewGuid(), status.CapturedAtUtc, status.ComputerName ?? Environment.MachineName,
                status.CpuPercent ?? -1, total, available,
                status.FixedDisks?.Select(disk => new DiskSnapshot(disk.Name ?? "", disk.TotalBytes, disk.FreeBytes)).ToList() ?? [],
                notes, LastBootAt: uptime, WindowsEdition: status.WindowsEdition, WindowsRelease: status.WindowsRelease,
                WindowsBuild: status.WindowsBuild, CpuTemperatureC: status.CpuTemperatureC,
                CpuTemperatureStatus: status.CpuTemperatureStatus, ActiveUptimeHours: status.UptimeHours,
                DiskReadLatencyMs: status.DiskReadLatencyMs, DiskWriteLatencyMs: status.DiskWriteLatencyMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    private sealed record Status(
        DateTimeOffset CapturedAtUtc,
        string? ComputerName,
        double? CpuPercent,
        double? MemoryUsedPercent,
        ulong? TotalMemoryBytes,
        ulong? AvailableMemoryBytes,
        double? CpuTemperatureC,
        string? CpuTemperatureStatus,
        double? UptimeHours,
        double? DiskReadLatencyMs,
        double? DiskWriteLatencyMs,
        string? WindowsEdition,
        string? WindowsRelease,
        int? WindowsBuild,
        List<StatusDisk>? FixedDisks,
        List<StatusIssue>? CriticalIssues);

    private sealed record StatusDisk(string? Name, long FreeBytes, long TotalBytes);
    private sealed record StatusIssue(string Identity, string Title, string Detail);
}
