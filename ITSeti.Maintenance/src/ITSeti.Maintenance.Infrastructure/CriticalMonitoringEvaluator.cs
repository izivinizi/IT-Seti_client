using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record CriticalMonitoringIssue(string Identity, string Title, string Detail);

public static class CriticalMonitoringEvaluator
{
    public static IReadOnlyList<CriticalMonitoringIssue> Evaluate(DiagnosticSnapshot snapshot)
    {
        var issues = new List<CriticalMonitoringIssue>();
        foreach (var sensor in snapshot.HwInfo?.AlertingSensors.Where(item => item.IsCritical) ?? [])
        {
            var identity = "hwinfo:critical:" + sensor.Name.ToLowerInvariant();
            var status = string.IsNullOrWhiteSpace(sensor.Status) ? "критическое состояние" : sensor.Status;
            issues.Add(new(identity, "HWiNFO сообщает критическое состояние",
                $"{sensor.Name}: {status} ({sensor.Value:N2} {sensor.Unit})."));
        }
        if (snapshot.CpuTemperatureC is >= HwInfoThresholds.CpuCriticalC)
            issues.Add(new("cpu:temperature", "Критическая температура CPU",
                $"Температура процессора достигла {snapshot.CpuTemperatureC:N0} °C."));
        if (snapshot.HwInfo?.VrmTemperatureC is >= HwInfoThresholds.VrmCriticalC)
            issues.Add(new("hwinfo:vrm-temperature", "Критическая температура VRM",
                $"Температура VRM достигла {snapshot.HwInfo.VrmTemperatureC:N0} °C."));
        if (snapshot.HwInfo?.PchTemperatureC is >= HwInfoThresholds.PchCriticalC)
            issues.Add(new("hwinfo:pch-temperature", "Критическая температура PCH",
                $"Температура чипсета PCH достигла {snapshot.HwInfo.PchTemperatureC:N0} °C."));
        if (snapshot.HwInfo?.Rail12V is { } rail12 && (rail12 < HwInfoThresholds.Rail12CriticalMinV || rail12 > HwInfoThresholds.Rail12CriticalMaxV))
            issues.Add(new("hwinfo:rail-12v", "Критическое отклонение шины 12 В",
                $"Напряжение шины 12 В: {rail12:N2} В."));
        if (snapshot.HwInfo?.Rail5V is { } rail5 && (rail5 < HwInfoThresholds.Rail5CriticalMinV || rail5 > HwInfoThresholds.Rail5CriticalMaxV))
            issues.Add(new("hwinfo:rail-5v", "Критическое отклонение шины 5 В",
                $"Напряжение шины 5 В: {rail5:N2} В."));
        if (snapshot.HwInfo?.Rail3V is { } rail3 && (rail3 < 2.97 || rail3 > 3.63))
            issues.Add(new("hwinfo:rail-3v", "Критическое отклонение шины 3,3 В", $"Напряжение шины 3,3 В: {rail3:N2} В."));
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
        if (latency >= 100)
            issues.Add(new("disk:latency", "Критическая задержка диска",
                $"Средняя задержка отклика диска держится на уровне {latency:N0} мс не менее 30 секунд."));
        return issues;
    }
}
