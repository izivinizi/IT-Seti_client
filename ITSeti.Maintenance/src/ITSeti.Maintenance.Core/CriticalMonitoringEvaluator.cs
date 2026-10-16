namespace ITSeti.Maintenance.Core;

public enum MonitoringIssueType { General, Voltage, Temperature, ResourceLoad, OtherHwInfoCritical }

public sealed record CriticalMonitoringIssue(string Identity, string Title, string Detail,
    DateTimeOffset? ObservedAtUtc = null, MonitoringIssueType Type = MonitoringIssueType.General,
    string Severity = "Critical");

public static class CriticalMonitoringEvaluator
{
    public static IReadOnlyList<CriticalMonitoringIssue> Evaluate(DiagnosticSnapshot snapshot)
    {
        var issues = new List<CriticalMonitoringIssue>();
        var hwInfoCapturedAt = snapshot.HwInfo?.CapturedAtUtc;
        var hwInfoIsFresh = IsRecent(hwInfoCapturedAt, snapshot.StartedAt);
        var temperatureCapturedAt = snapshot.CpuTemperatureCapturedAtUtc ?? hwInfoCapturedAt ?? snapshot.StartedAt;
        if (snapshot.CpuTemperatureC is >= HwInfoThresholds.CpuCriticalC &&
            IsRecent(temperatureCapturedAt, snapshot.StartedAt))
            issues.Add(new("cpu:temperature", "Критическая температура CPU",
                $"Температура процессора достигла {snapshot.CpuTemperatureC:N0} °C. " +
                HwInfoConfirmation(hwInfoIsFresh,
                    snapshot.HwInfo?.CpuTemperatureC is { } cpuTemp ? cpuTemp >= HwInfoThresholds.CpuCriticalC : null),
                temperatureCapturedAt, MonitoringIssueType.Temperature));

        if (hwInfoIsFresh && snapshot.HwInfo?.VrmTemperatureC is >= HwInfoThresholds.VrmCriticalC)
            issues.Add(new("hwinfo:vrm-temperature", "Критическая температура VRM",
                $"Температура VRM достигла {snapshot.HwInfo.VrmTemperatureC:N0} °C. Показание HWiNFO превышает порог ИТ-Сети.",
                hwInfoCapturedAt, MonitoringIssueType.Temperature));
        if (hwInfoIsFresh && snapshot.HwInfo?.PchTemperatureC is >= HwInfoThresholds.PchCriticalC)
            issues.Add(new("hwinfo:pch-temperature", "Критическая температура PCH",
                $"Температура чипсета PCH достигла {snapshot.HwInfo.PchTemperatureC:N0} °C. Показание HWiNFO превышает порог ИТ-Сети.",
                hwInfoCapturedAt, MonitoringIssueType.Temperature));
        if (hwInfoIsFresh && snapshot.HwInfo?.Rail12V is { } rail12 &&
            (rail12 < HwInfoThresholds.Rail12CriticalMinV || rail12 > HwInfoThresholds.Rail12CriticalMaxV))
            issues.Add(new("hwinfo:rail-12v", "Критическое отклонение шины 12 В",
                $"Напряжение шины 12 В: {rail12:N2} В. Показание HWiNFO превышает порог ИТ-Сети.", hwInfoCapturedAt, MonitoringIssueType.Voltage));
        if (hwInfoIsFresh && snapshot.HwInfo?.Rail5V is { } rail5 &&
            (rail5 < HwInfoThresholds.Rail5CriticalMinV || rail5 > HwInfoThresholds.Rail5CriticalMaxV))
            issues.Add(new("hwinfo:rail-5v", "Критическое отклонение шины 5 В",
                $"Напряжение шины 5 В: {rail5:N2} В. Показание HWiNFO превышает порог ИТ-Сети.", hwInfoCapturedAt, MonitoringIssueType.Voltage));
        if (hwInfoIsFresh && snapshot.HwInfo?.Rail3V is { } rail3 && (rail3 < 2.97 || rail3 > 3.63))
            issues.Add(new("hwinfo:rail-3v", "Критическое отклонение шины 3,3 В",
                $"Напряжение шины 3,3 В: {rail3:N2} В. Показание HWiNFO превышает порог ИТ-Сети.", hwInfoCapturedAt, MonitoringIssueType.Voltage));

        var cpuLoadCapturedAt = snapshot.CpuPercentCapturedAtUtc ?? snapshot.StartedAt;
        if (snapshot.CpuPercent is >= HwInfoThresholds.CpuLoadCriticalPercent and <= 100 &&
            IsRecent(cpuLoadCapturedAt, snapshot.StartedAt))
            issues.Add(new("cpu:load", "Высокая загрузка CPU",
                $"Загрузка процессора держится на уровне {snapshot.CpuPercent:N0}% " +
                $"(порог {HwInfoThresholds.CpuLoadCriticalPercent:N0}%). " +
                HwInfoConfirmation(hwInfoIsFresh,
                    snapshot.HwInfo?.CpuLoadPercent is { } cpuLoad ? cpuLoad >= HwInfoThresholds.CpuLoadCriticalPercent : null),
                cpuLoadCapturedAt, MonitoringIssueType.ResourceLoad, "Warning"));

        var memoryCapturedAt = snapshot.MemoryUsedPercentCapturedAtUtc ?? snapshot.StartedAt;
        if (IsRecent(memoryCapturedAt, snapshot.StartedAt) && snapshot.TotalMemoryBytes > 0 &&
            snapshot.AvailableMemoryBytes <= snapshot.TotalMemoryBytes)
        {
            var memoryUsedPercent = 100.0 * (snapshot.TotalMemoryBytes - snapshot.AvailableMemoryBytes) / snapshot.TotalMemoryBytes;
            if (memoryUsedPercent >= HwInfoThresholds.MemoryUsedCriticalPercent)
                issues.Add(new("memory:load", "Высокая загрузка памяти",
                    $"Занято {memoryUsedPercent:N0}% оперативной памяти " +
                    $"(порог {HwInfoThresholds.MemoryUsedCriticalPercent:N0}%). " +
                    HwInfoConfirmation(hwInfoIsFresh,
                        snapshot.HwInfo?.MemoryLoadPercent is { } memoryLoad ? memoryLoad >= HwInfoThresholds.MemoryUsedCriticalPercent : null),
                    memoryCapturedAt, MonitoringIssueType.ResourceLoad, "Warning"));
        }

        // Shared Memory does not expose HWiNFO's coloured alarm state for general readings.
        // Explicit alarm/status fields from HWiNFO exports are still preserved and require two
        // distinct frames before they become a warning in the continuous monitor.
        if (hwInfoIsFresh)
        {
            foreach (var sensor in snapshot.HwInfo!.AlertingSensors)
            {
                var identity = KnownSensorIdentity(sensor.Category) ??
                    "hwinfo:critical:" + (sensor.SensorId ?? sensor.Name).ToLowerInvariant();
                if (issues.Any(issue => issue.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase))) continue;
                var type = GetIssueType(sensor.Category);
                var severity = type is MonitoringIssueType.OtherHwInfoCritical or MonitoringIssueType.ResourceLoad || !sensor.IsCritical ? "Warning" : "Critical";
                var title = severity == "Warning" ? "Датчик HWiNFO требует проверки" : "HWiNFO сообщает критическое состояние";
                var detail = sensor.Category == "HardwareAlarm"
                    ? $"{sensor.Name}: {sensor.Status ?? HwInfoSensorPolicy.DescribeAlarm(sensor.Name)}. HWiNFO: аппаратный флаг активен (Yes)."
                    : $"{sensor.Name}: {sensor.Status ?? "предупреждение датчика"} ({sensor.Value:N2} {sensor.Unit}); статус передан HWiNFO.";
                issues.Add(new(identity, title, detail, hwInfoCapturedAt, type, severity));
            }
        }

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
        foreach (var disk in snapshot.DiskLatencies ?? [])
        {
            var latency = Math.Max(disk.ReadMs ?? 0, disk.WriteMs ?? 0);
            if (latency >= 100 && !double.IsInfinity(latency) && !double.IsNaN(latency))
                issues.Add(new("disk:latency:" + disk.Disk.ToLowerInvariant(), $"Высокая задержка диска {disk.Disk}",
                    $"Диск {disk.Disk} · чтение {disk.ReadMs:N0} мс, запись {disk.WriteMs:N0} мс (порог 100 мс в течение 30 секунд)."));
        }
        if (snapshot.DiskLatencies is null && Math.Max(snapshot.DiskReadLatencyMs ?? 0, snapshot.DiskWriteLatencyMs ?? 0) is var totalLatency && totalLatency >= 100)
            issues.Add(new("disk:latency", "Высокая задержка диска — накопитель не определён",
                $"Суммарный счётчик Windows: {totalLatency:N0} мс. Этот старый отчёт не содержит отдельных показаний накопителей."));
        return issues;
    }

    private static string? KnownSensorIdentity(string category) => category switch
    {
        "CpuTemperature" => "cpu:temperature",
        "VrmTemperature" => "hwinfo:vrm-temperature",
        "PchTemperature" => "hwinfo:pch-temperature",
        "Rail12V" => "hwinfo:rail-12v",
        "Rail5V" => "hwinfo:rail-5v",
        "Rail3V" => "hwinfo:rail-3v",
        "CpuLoad" => "cpu:load",
        "MemoryLoad" => "memory:load",
        _ => null
    };

    private static MonitoringIssueType GetIssueType(string category) => category switch
    {
        "Rail12V" or "Rail5V" or "Rail3V" or "CpuVoltage" => MonitoringIssueType.Voltage,
        "CpuTemperature" or "VrmTemperature" or "PchTemperature" => MonitoringIssueType.Temperature,
        "CpuLoad" or "MemoryLoad" => MonitoringIssueType.ResourceLoad,
        _ => MonitoringIssueType.OtherHwInfoCritical
    };

    private static string HwInfoConfirmation(bool isFresh, bool? confirmed) =>
        !isFresh || confirmed is null ? "HWiNFO: нет свежего показания для проверки." :
        confirmed.Value ? "Показание HWiNFO превышает порог ИТ-Сети." :
            "Показание HWiNFO не превышает порог ИТ-Сети.";

    private static bool IsRecent(DateTimeOffset? capturedAtUtc, DateTimeOffset snapshotAt)
    {
        if (capturedAtUtc is not { } captured) return false;
        var age = snapshotAt - captured;
        return age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(15);
    }
}
