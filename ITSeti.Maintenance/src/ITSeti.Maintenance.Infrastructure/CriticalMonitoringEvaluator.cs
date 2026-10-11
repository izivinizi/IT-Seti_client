using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record CriticalMonitoringIssue(string Identity, string Title, string Detail);

public static class CriticalMonitoringEvaluator
{
    public static IReadOnlyList<CriticalMonitoringIssue> Evaluate(DiagnosticSnapshot snapshot)
    {
        var issues = new List<CriticalMonitoringIssue>();
        if (snapshot.CpuTemperatureC is >= 90)
            issues.Add(new("cpu:temperature", "Критическая температура CPU",
                $"Температура процессора достигла {snapshot.CpuTemperatureC:N0} °C."));
        if (snapshot.HwInfo?.VrmTemperatureC is >= 105)
            issues.Add(new("hwinfo:vrm-temperature", "Критическая температура VRM",
                $"Температура VRM достигла {snapshot.HwInfo.VrmTemperatureC:N0} °C."));
        if (snapshot.HwInfo?.Rail12V is { } rail12 && (rail12 < 10.8 || rail12 > 13.2))
            issues.Add(new("hwinfo:rail-12v", "Критическое отклонение шины 12 В",
                $"Напряжение шины 12 В: {rail12:N2} В."));
        if (snapshot.HwInfo?.Rail5V is { } rail5 && (rail5 < 4.5 || rail5 > 5.5))
            issues.Add(new("hwinfo:rail-5v", "Критическое отклонение шины 5 В",
                $"Напряжение шины 5 В: {rail5:N2} В."));
        if (snapshot.CpuPercent is >= 98)
            issues.Add(new("cpu:load", "Критическая загрузка CPU",
                $"Загрузка процессора достигла {snapshot.CpuPercent:N0}%."));
        if (snapshot.MemoryUsedPercent >= 98)
            issues.Add(new("memory:load", "Критическая загрузка памяти",
                $"Используется {snapshot.MemoryUsedPercent:N0}% оперативной памяти."));
        foreach (var disk in snapshot.Disks.Where(item => item.TotalBytes >= 50L * 1073741824 &&
                     item.FreeBytes < 5L * 1073741824))
            issues.Add(new("space:" + disk.Name.TrimEnd('\\').ToLowerInvariant(),
                $"Критически мало места на диске {disk.Name}",
                $"Свободно {disk.FreeBytes / 1073741824.0:N1} ГБ."));
        foreach (var disk in snapshot.QuickDisks ?? [])
        {
            if (disk.Health.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                disk.Health.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) ||
                disk.Health.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                disk.Health.Contains("Тревог", StringComparison.OrdinalIgnoreCase) ||
                disk.Health.Contains("Плох", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("disk:health:" + disk.Model.ToLowerInvariant(),
                    $"Накопитель {disk.Model} сообщает критическое состояние", disk.Health));
        }
        var latency = Math.Max(snapshot.DiskReadLatencyMs ?? 0, snapshot.DiskWriteLatencyMs ?? 0);
        if (latency >= 250)
            issues.Add(new("disk:latency", "Критическая задержка диска",
                $"Средняя задержка отклика диска достигла {latency:N0} мс."));
        return issues;
    }
}
