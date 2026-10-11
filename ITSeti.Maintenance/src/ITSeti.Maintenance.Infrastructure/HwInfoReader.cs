using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public static class HwInfoReader
{
    private static readonly Regex NumberPattern = new(@"[-+]?\d+(?:[\.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly object SessionGate = new();
    private static DateTimeOffset sessionStartedUtc;
    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromHours(10);
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "hwinfo.json");

    public static bool SessionRestartDue
    {
        get
        {
            lock (SessionGate)
                return sessionStartedUtc != default && DateTimeOffset.UtcNow - sessionStartedUtc >= SessionLifetime;
        }
    }

    public static void ResetSession()
    {
        lock (SessionGate) sessionStartedUtc = DateTimeOffset.UtcNow;
        try { File.Delete(CachePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task<HwInfoSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (SessionGate)
        {
            if (sessionStartedUtc == default) sessionStartedUtc = DateTimeOffset.UtcNow;
        }
        var executable = FindExecutable();
        if (executable is null) return null;
        var temporary = Path.Combine(Path.GetTempPath(), "ITSeti-HWiNFO-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            } };
            process.StartInfo.ArgumentList.Add("-j" + temporary);
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length is <= 0 or > 8 * 1024 * 1024) return null;
            var snapshot = Parse(await File.ReadAllTextAsync(temporary, cancellationToken), executable);
            if (snapshot is not null) await WriteCacheAsync(snapshot, cancellationToken);
            return snapshot;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return null; }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    public static HwInfoSnapshot? ReadCached(TimeSpan maximumAge)
    {
        try
        {
            if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > maximumAge) return null;
            return Parse(File.ReadAllText(CachePath), "cached");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static HwInfoSnapshot? Parse(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var readings = new List<HwInfoSensorReading>();
        CollectSensors(document.RootElement, "", readings);
        if (readings.Count == 0 && FindText(document.RootElement, "sensor") is null) return null;
        return new HwInfoSnapshot(source, FindText(document.RootElement, "version") ?? "",
            DateTimeOffset.UtcNow, FindText(document.RootElement, "cpu", "processor"),
            FindText(document.RootElement, "motherboard", "mainboard"),
            FindText(document.RootElement, "bios"), FindText(document.RootElement, "memory type", "memorytype", "memory"), readings);
    }

    private static async Task WriteCacheAsync(HwInfoSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot), cancellationToken);
            File.Move(temporary, CachePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? FindExecutable()
    {
        var applicationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ITSETI_HWINFO_PATH"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "HWiNFO", "HWiNFO32.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "HWiNFO", "HWiNFO64.exe"),
            applicationRoot is null ? null : Path.Combine(applicationRoot, "Tools", "HWiNFO", "HWiNFO32.exe"),
            applicationRoot is null ? null : Path.Combine(applicationRoot, "Tools", "HWiNFO", "HWiNFO64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HWiNFO", "HWiNFO64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "HWiNFO", "HWiNFO32.exe"),
            @"C:\HWiNFO\HWiNFOPortable.exe", @"C:\HWiNFO\HWiNFO32.exe", @"C:\HWiNFO\HWiNFO64.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "Tools", "HWiNFO", "HWiNFO32.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "Tools", "HWiNFO", "HWiNFO64.exe")
        };
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).FirstOrDefault(File.Exists);
    }

    private static void CollectSensors(JsonElement element, string context, ICollection<HwInfoSensorReading> readings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var label = ReadText(element, "label", "name", "sensorName", "description", "title");
            var unit = ReadText(element, "unit", "units") ?? "";
            var value = ReadNumber(element, "value", "current", "reading", "rawValue");
            if (label is not null && value is { } reading)
            {
                var fullName = string.IsNullOrWhiteSpace(context) ? label : context + " / " + label;
                var category = Classify(fullName, unit);
                if (category is not null) readings.Add(new(fullName, reading, unit, category));
            }
            foreach (var property in element.EnumerateObject())
            {
                var next = string.IsNullOrWhiteSpace(context) ? property.Name : context + " / " + property.Name;
                CollectSensors(property.Value, next, readings);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectSensors(item, context, readings);
    }

    private static string? Classify(string name, string unit)
    {
        var lower = name.ToLowerInvariant();
        if (unit.Contains("%", StringComparison.Ordinal) && (lower.Contains("cpu") || lower.Contains("processor")) &&
            (lower.Contains("load") || lower.Contains("usage") || lower.Contains("util"))) return "CpuLoad";
        if (unit.Contains("c", StringComparison.OrdinalIgnoreCase) || lower.Contains("temperature") || lower.Contains("temp"))
        {
            if (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop")) return "VrmTemperature";
            if (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller")) return "PchTemperature";
            if (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie")) return "CpuTemperature";
            return null;
        }
        if (unit.Trim().Equals("V", StringComparison.OrdinalIgnoreCase) || lower.Contains("voltage") || lower.Contains(" vcore"))
        {
            if (lower.Contains("12v") || lower.Contains("+12")) return "Rail12V";
            if (lower.Contains("5v") || lower.Contains("+5")) return "Rail5V";
            if (lower.Contains("vcore") || lower.Contains("vcc") || lower.Contains("vsoc") || lower.Contains("vsc") || lower.Contains("cpu core")) return "CpuVoltage";
        }
        return null;
    }

    private static string? ReadText(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
        return null;
    }

    private static double? ReadNumber(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number)) return number;
            if (property.Value.ValueKind == JsonValueKind.String && TryParseNumber(property.Value.GetString(), out number)) return number;
        }
        return null;
    }

    private static bool TryParseNumber(string? value, out double number)
    {
        number = 0;
        var match = NumberPattern.Match(value ?? "");
        return match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string? FindText(JsonElement root, params string[] tokens)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
                if (tokens.Any(token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            foreach (var property in root.EnumerateObject())
                if (FindText(property.Value, tokens) is { } nested) return nested;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray()) if (FindText(item, tokens) is { } nested) return nested;
        return null;
    }
}
