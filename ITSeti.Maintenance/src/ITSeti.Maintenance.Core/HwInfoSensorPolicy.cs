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
        if (lower.Contains("gpu") || lower.Contains("graphics") || lower.Contains("video") ||
            lower.Contains("distance to") || lower.Contains("tjmax") || lower.Contains("thermal limit")) return null;
        if (units == "%")
        {
            if (lower.Contains("physical memory load")) return "MemoryLoad";
            // Per-core usage and CPU utility are not total CPU utilization (utility can exceed 100%).
            if (lower.Contains("total cpu usage") || lower.Contains("total cpu load")) return "CpuLoad";
            return null;
        }
        if (units.Equals("Yes/No", StringComparison.OrdinalIgnoreCase) &&
            (lower.Contains("thermal throttling") || lower.Contains("critical temperature") || lower.Contains("thermal trip"))) return "HardwareAlarm";
        if (units.Equals("°C", StringComparison.OrdinalIgnoreCase) || units.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            if (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop")) return "VrmTemperature";
            if (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller") || lower.Contains("чипсет")) return "PchTemperature";
            if (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie") || lower.Contains("процессор")) return "CpuTemperature";
            return null;
        }
        if (!units.Equals("V", StringComparison.OrdinalIgnoreCase)) return null;
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
        "CpuTemperature" or "VrmTemperature" or "PchTemperature" => value >= 5 && value <= 150,
        "HardwareAlarm" => value == 0 || value == 1,
        "CpuVoltage" => value > 0 && value <= 3,
        "Rail12V" or "Rail5V" or "Rail3V" => value >= 0 && value <= 24,
        _ => false
    };
}
