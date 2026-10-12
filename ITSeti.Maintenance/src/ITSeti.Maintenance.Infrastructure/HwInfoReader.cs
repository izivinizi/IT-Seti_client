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
    private static Process? sensorProcess;
    private static DateTimeOffset nextStartAttemptUtc;
    private static string SessionMarker => Path.ChangeExtension(CachePath,"session.txt");
    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromHours(10);
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "hwinfo.json");
    public static string ErrorPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "hwinfo-error.txt");

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
        lock (SessionGate)
        {
            if (sensorProcess is not null)
            {
                try { if (!sensorProcess.HasExited) sensorProcess.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                sensorProcess.Dispose(); sensorProcess = null;
            }
            sessionStartedUtc = DateTimeOffset.UtcNow;
            HwInfoVoltageStabilizer.Reset();
            SaveSessionMarker();
            nextStartAttemptUtc = default;
        }
        try { File.Delete(CachePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void StopSession()
    {
        lock(SessionGate)
        {
            if(sensorProcess is null)return;
            try {if(!sensorProcess.HasExited)sensorProcess.Kill(entireProcessTree:true);}
            catch(Exception ex) when(ex is Win32Exception or InvalidOperationException){}
            sensorProcess.Dispose();sensorProcess=null;
        }
    }

    public static async Task<HwInfoSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (SessionGate)
        {
            if (sessionStartedUtc == default)
            {
                try { if (File.Exists(SessionMarker)) DateTimeOffset.TryParse(File.ReadAllText(SessionMarker), out sessionStartedUtc); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if(sessionStartedUtc == default || sessionStartedUtc > DateTimeOffset.UtcNow) {sessionStartedUtc=DateTimeOffset.UtcNow;SaveSessionMarker();}
            }
        }
        if(SessionRestartDue) ResetSession();
        var executable = FindExecutable();
        if (executable is null) { WriteFailure("HWiNFO не найден в комплекте приложения."); return null; }
        try
        {
            lock (SessionGate)
            {
                if ((sensorProcess is null || sensorProcess.HasExited) && DateTimeOffset.UtcNow >= nextStartAttemptUtc)
                {
                    nextStartAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                    sensorProcess?.Dispose();
                    sensorProcess = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(executable)!
                    });
                    if (sensorProcess is not null) HwInfoVoltageStabilizer.Reset();
                }
            }
            ITSeti.HwInfoBridge.SensorValue[]? values = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if(sensorProcess is not null && !sensorProcess.HasExited) ITSeti.HwInfoBridge.SensorReader.StartSensors(sensorProcess.Id);
                try { values = ITSeti.HwInfoBridge.SensorReader.Read(); break; }
                catch (FileNotFoundException) when (attempt < 2) { await Task.Delay(250, cancellationToken); }
            }
            var readings = (values ?? []).Select(value => new { value, category = Classify(value.Name, value.Unit) })
                .Where(item => item.category is not null)
                .Select(item => new HwInfoSensorReading(item.value.Name, item.value.Value, item.value.Unit, item.category!,
                    IsCritical:item.category=="HardwareAlarm" && item.value.Value>0 && !item.value.Unit.Contains("C",StringComparison.OrdinalIgnoreCase))).ToArray();
            readings = HwInfoVoltageStabilizer.Stabilize(readings).ToArray();
            var snapshot = readings.Length == 0 ? null : new HwInfoSnapshot("HWiNFO Shared Memory", FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "",
                DateTimeOffset.UtcNow, null, null, null, null, readings);
            if (snapshot is not null)
            {
                await WriteCacheAsync(snapshot, cancellationToken);
                try { File.Delete(ErrorPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else WriteFailure("HWiNFO не передал датчики. Проверьте режим Sensors-only и Shared Memory Support.");
            return snapshot;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { WriteFailure(ex.GetType().Name + ": " + ex.Message); return null; }
    }

    public static HwInfoSnapshot? ReadCached(TimeSpan maximumAge)
    {
        try
        {
            if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > maximumAge) return null;
            var snapshot=JsonSerializer.Deserialize<HwInfoSnapshot>(File.ReadAllText(CachePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(snapshot?.Sensors is null || snapshot.Sensors.Any(sensor=>sensor is null || !double.IsFinite(sensor.Value)))return null;
            var age=DateTimeOffset.UtcNow-snapshot.CapturedAtUtc;
            return age>=TimeSpan.FromSeconds(-60)&&age<=maximumAge?snapshot:null;
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

    internal static HwInfoSnapshot? ParseSensorCsv(string csv, string source)
    {
        var lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        var separator = DetectSeparator(lines[0]);
        var headers = SplitCsvLine(lines[0], separator);
        var readings = new List<HwInfoSensorReading>();
        foreach (var line in lines.Skip(1))
        {
            var values = SplitCsvLine(line, separator);
            if (values.Count < Math.Min(headers.Count, 2)) continue;
            for (var index = 0; index < Math.Min(headers.Count, values.Count); index++)
            {
                var name = headers[index].Trim();
                if (name.Length == 0 || name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Time", StringComparison.OrdinalIgnoreCase) || name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryParseNumber(values[index], out var value)) continue;
                var unitMatch = Regex.Match(name, @"\[(?<unit>[^\]]+)\]\s*$", RegexOptions.CultureInvariant);
                var unit = unitMatch.Success ? unitMatch.Groups["unit"].Value : "";
                var category = Classify(name, unit);
                if (category is not null) readings.Add(new(name, value, unit, category));
            }
        }
        if (readings.Count == 0) return null;
        string version;
        try { version = FileVersionInfo.GetVersionInfo(source).FileVersion ?? ""; }
        catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException) { version = ""; }
        return new(source, version, DateTimeOffset.UtcNow, null, null, null, null, readings);
    }

    private static char DetectSeparator(string header)
    {
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(candidate => SplitCsvLine(header, candidate).Count).First();
    }

    private static List<string> SplitCsvLine(string line, char separator)
    {
        var fields = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == separator && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        fields.Add(value.ToString());
        return fields;
    }

    private static string ShortMessage(params string[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).ReplaceLineEndings(" ").Trim() is { Length: > 300 } text
            ? text[..300] : string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value))).ReplaceLineEndings(" ").Trim();

    private static void WriteFailure(string message)
    {
        ApplicationErrorStore.Record("HWiNFO", "Чтение датчиков", new InvalidOperationException(message));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorPath)!);
            File.WriteAllText(ErrorPath, DateTimeOffset.Now.ToString("O") + Environment.NewLine + message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task WriteCacheAsync(HwInfoSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                snapshot.Source,
                snapshot.ToolVersion,
                snapshot.CapturedAtUtc,
                snapshot.CpuName,
                snapshot.Motherboard,
                snapshot.BiosVersion,
                snapshot.MemoryType,
                snapshot.Sensors,
                snapshot.CpuTemperatureC,
                snapshot.VrmTemperatureC,
                snapshot.PchTemperatureC,
                snapshot.Rail12V,
                snapshot.Rail5V,
                snapshot.Rail3V,
                snapshot.CpuVoltageV,
                snapshot.CpuLoadPercent
            }), cancellationToken);
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
                if (category is not null)
                {
                    var sensorStatus = ReadSensorStatus(element);
                    var isCritical = ReadFlag(element, "critical", "isCritical", "criticalAlarm", "isCriticalAlarm")
                        || IsCriticalStatus(sensorStatus);
                    var isWarning = ReadFlag(element, "warning", "isWarning", "warningAlarm", "isWarningAlarm")
                        || IsWarningStatus(sensorStatus);
                    readings.Add(new(fullName, reading, unit, category, sensorStatus, isWarning, isCritical));
                }
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
        if(lower.Contains("distance to") || lower.Contains("tjmax") || lower.Contains("thermal limit"))return null;
        if(unit.Contains('%') && lower.Contains("physical memory load")) return "MemoryLoad";
        if(lower.Contains("thermal throttling") || lower.Contains("critical temperature") || lower.Contains("thermal trip")) return "HardwareAlarm";
        if (unit.Contains("%", StringComparison.Ordinal) && (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("процессор")) &&
            (lower.Contains("load") || lower.Contains("usage") || lower.Contains("util") || lower.Contains("загруз") || lower.Contains("использ"))) return "CpuLoad";
        if (unit.Contains("c", StringComparison.OrdinalIgnoreCase) || lower.Contains("temperature") || lower.Contains("temp") || lower.Contains("температур"))
        {
            if (lower.Contains("gpu") || lower.Contains("graphics") || lower.Contains("video")) return null;
            if (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop")) return "VrmTemperature";
            if (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller") || lower.Contains("чипсет")) return "PchTemperature";
            if (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie") || lower.Contains("процессор")) return "CpuTemperature";
            return null;
        }
        if (unit.Trim().Equals("V", StringComparison.OrdinalIgnoreCase) || lower.Contains("voltage") || lower.Contains(" vcore"))
        {
            if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*3[.,]3\s*v(?![a-z0-9])", RegexOptions.CultureInvariant)) return "Rail3V";
            if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*12(?:[.,]0{1,2})?\s*v(?![a-z0-9])", RegexOptions.CultureInvariant)) return "Rail12V";
            if (Regex.IsMatch(lower, @"(?<![a-z0-9])\+?\s*5(?:[.,]0{1,2})?\s*v(?:s\s*b)?(?![a-z0-9])", RegexOptions.CultureInvariant)) return "Rail5V";
            if (lower.Contains("vcore") || lower.Contains("vsoc") || lower.Contains("vsc") || lower.Contains("cpu core")) return "CpuVoltage";
            return "Voltage";
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

    private static void SaveSessionMarker()
    {
        try {Directory.CreateDirectory(Path.GetDirectoryName(SessionMarker)!);File.WriteAllText(SessionMarker,sessionStartedUtc.ToString("O"));}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ApplicationErrorStore.Record("HWiNFO","Сохранение цикла датчиков",ex);}
    }

    private static string? ReadSensorStatus(JsonElement element) =>
        ReadText(element, "status", "state", "health", "condition", "severity", "alarm", "alert");

    private static bool ReadFlag(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            if (property.Value.ValueKind == JsonValueKind.True) return true;
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString();
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
        }
        return false;
    }

    private static bool IsCriticalStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Regex.IsMatch(status,
            "critical|crit|alarm|failed|failure|bad|опас|крит", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsWarningStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Regex.IsMatch(status,
            "warning|warn|attention|предуп|вним", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
