using System.Text.RegularExpressions;
namespace ITSeti.Maintenance.Core;

/// <summary>Unit-aware sensor selection shared by modern and Windows 7 adapters.</summary>
public static class HwInfoSensorPolicy
{
    public static string? Classify(string? name, string? unit)
    {
        if (string.IsNullOrWhiteSpace(name) || unit is null) return null;
        var units = unit.Trim();
        // Most Shared Memory readings are clocks, counters or speeds. Reject their
        // units before allocating a normalized name; both client adapters use this path.
        if (units != "%" && !units.Equals("Yes/No", StringComparison.OrdinalIgnoreCase) &&
            !units.Equals("°C", StringComparison.OrdinalIgnoreCase) &&
            !units.Equals("C", StringComparison.OrdinalIgnoreCase) &&
            !units.Equals("V", StringComparison.OrdinalIgnoreCase)) return null;
        var lower = name!.ToLowerInvariant();
        if (units.Equals("Yes/No", StringComparison.OrdinalIgnoreCase))
            return IsAlarmName(lower) ? "HardwareAlarm" : null;
        if (lower.Contains("distance to") || lower.Contains("tjmax")) return null;
        if (units == "%")
        {
            if (lower.Contains("physical memory load")) return "MemoryLoad";
            // Per-core usage and CPU utility are not total CPU utilization (utility can exceed 100%).
            if (lower.Contains("total cpu usage") || lower.Contains("total cpu load")) return "CpuLoad";
            return null;
        }
        if (units.Equals("°C", StringComparison.OrdinalIgnoreCase) || units.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            if (lower.Contains("gpu") || lower.Contains("graphics") || lower.Contains("video"))
                return lower.Contains("memory") || lower.Contains("vram") ? "GpuMemoryTemperature" : "GpuTemperature";
            if (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop") ||
                lower.Contains("vr vcc") || lower.Contains("vr vdd") || lower.Contains("voltage regulator")) return "VrmTemperature";
            if (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller") || lower.Contains("чипсет")) return "PchTemperature";
            if (lower.Contains("dimm") || lower.Contains("dram") || lower.Contains("memory")) return "MemoryTemperature";
            if (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie") || lower.Contains("процессор")) return "CpuTemperature";
            return "OtherTemperature";
        }
        if (!units.Equals("V", StringComparison.OrdinalIgnoreCase)) return null;
        if (lower.Contains("gpu") || lower.Contains("graphics") || lower.Contains("video")) return null;
        // Standby rails measure a different circuit and must not replace a suspicious main PSU rail.
        if (lower.Contains("standby") || lower.Contains("5vsb") || lower.Contains("3vsb")) return null;
        if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*3[.,]3\s*v(?![a-z0-9])")) return "Rail3V";
        if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*12(?:[.,]0{1,2})?\s*v(?![a-z0-9])")) return "Rail12V";
        if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*5(?:[.,]0{1,2})?\s*v(?![a-z0-9])")) return "Rail5V";
        if (!lower.Contains("vid") && !lower.Contains("soc") && (lower.Contains("vcore") || lower.Contains("cpu core voltage"))) return "CpuVoltage";
        return null;
    }

    public static bool IsValidValue(string category, double value) => Numeric.IsFinite(value) && category switch
    {
        "CpuLoad" or "MemoryLoad" => value >= 0 && value <= 100,
        "CpuTemperature" or "VrmTemperature" or "PchTemperature" or "GpuTemperature" or "GpuMemoryTemperature" or
            "MemoryTemperature" or "OtherTemperature" => value >= 0 && value <= 150,
        "HardwareAlarm" => value == 0 || value == 1,
        "CpuVoltage" => value > 0 && value <= 3,
        "Rail12V" or "Rail5V" or "Rail3V" => value >= 0 && value <= 24,
        _ => false
    };

    public static bool IsAlarmName(string name)
    {
        var lower = name.ToLowerInvariant();
        // A normal boolean state (AC connected, GPU utilization limit, etc.) is
        // not an alarm. Hardware flags have their own names, independent of CPU/GPU.
        return lower.Contains("throttl") || lower.Contains("critical") || lower.Contains("thermal trip") ||
            lower.Contains("thermal alert") || lower.Contains("overheat") || lower.Contains("prochot") ||
            lower.Contains("alarm") || lower.Contains("warning") || lower.Contains("failure") ||
            lower.Contains("fault") || lower.Contains("error") || lower.Contains("limit exceeded") ||
            lower.Contains("performance limit") && (lower.Contains("thermal") || lower.Contains("power") || lower.Contains("voltage"));
    }

    public static string DescribeAlarm(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("thermal") || lower.Contains("overheat") || lower.Contains("prochot"))
            return "Аппаратное ограничение из-за температуры: проверьте охлаждение и температуры компонента";
        if (lower.Contains("power") || lower.Contains("current limit"))
            return "Достигнут лимит мощности или тока: возможно штатное ограничение производительности под нагрузкой";
        if (lower.Contains("voltage")) return "Ограничение по напряжению: проверьте питание и режим работы компонента";
        if (lower.Contains("fan")) return "Сигнал неисправности вентилятора: проверьте его вращение и подключение";
        if (lower.Contains("drive") || lower.Contains("disk") || lower.Contains("s.m.a.r.t"))
            return "Аппаратное предупреждение накопителя: проверьте SMART и сохранность данных";
        return "Аппаратный флаг HWiNFO активен: проверьте указанный компонент и его диагностические данные";
    }

    public static IReadOnlyList<HwInfoSensorReading> DisplaySensors(IEnumerable<HwInfoSensorReading> sensors)
    {
        var readings = sensors.Where(item => item.Category != "HardwareAlarm").ToArray();
        var result = readings.Where(item => item.Category != "CpuTemperature").ToList();
        // Prefer the aggregate supplied by HWiNFO. Retain raw per-core samples
        // in diagnostics/monitoring, but do not flood the sensor screen with them.
        foreach (var group in readings.Where(item => item.Category == "CpuTemperature")
                     .GroupBy(item => Regex.Match(item.Name, @"(?i)CPU \[#\d+\]").Value))
        {
            var selected = group.OrderByDescending(item => CpuDisplayPriority(item.Name)).First();
            if (CpuDisplayPriority(selected.Name) < 3 && group.Count() > 1)
                selected = selected with { Name = (group.Key.Length > 0 ? group.Key : "CPU") + " / Средняя температура ядер",
                    Value = group.Average(item => item.Value), Quality = "DerivedDisplayAverage" };
            result.Add(selected);
        }
        return result.GroupBy(item => item.Category + "|" + item.Name)
            .Select(group => group.OrderByDescending(item => item.Value).First())
            .OrderBy(item => item.Category).ThenBy(item => item.Name).ToArray();
    }

    private static int CpuDisplayPriority(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("average") || lower.Contains("средн") ? 5 :
            lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie") ? 4 :
            lower.Contains("core max") ? 3 : lower.Contains("core") ? 1 : 2;
    }

    public static string DisplayCategory(string category) => category switch
    {
        "CpuLoad" or "CpuTemperature" => "Процессор",
        "MemoryLoad" or "MemoryTemperature" => "Оперативная память",
        "GpuTemperature" or "GpuMemoryTemperature" => "Видеокарта",
        "VrmTemperature" or "PchTemperature" or "OtherTemperature" => "Плата и охлаждение",
        _ => "Питание"
    };
}
