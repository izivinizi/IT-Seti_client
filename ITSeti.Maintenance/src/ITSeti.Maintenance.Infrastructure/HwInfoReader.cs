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
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly object SessionGate = new();
    private static readonly SemaphoreSlim ReadGate = new(1, 1);
    private static DateTimeOffset sessionStartedUtc;
    private static Process? sensorProcess;
    private static DateTimeOffset sensorProcessStartedUtc;
    private static DateTimeOffset nextStartAttemptUtc;
    private static DateTimeOffset nextRecoveryUtc;
    private static DateTimeOffset nextFailureWriteUtc;
    private static string? lastFailureMessage;
    private static int missingMemoryReads;
    private static int staleSourceReads;
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
            sensorProcessStartedUtc = default;
            sessionStartedUtc = DateTimeOffset.UtcNow;
            HwInfoVoltageStabilizer.Reset();
            SaveSessionMarker();
            nextStartAttemptUtc = default;
            missingMemoryReads = 0;
            staleSourceReads = 0;
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
            sensorProcessStartedUtc = default;
        }
    }

    public static async Task<HwInfoSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await ReadGate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { ReadGate.Release(); }
    }

    private static async Task<HwInfoSnapshot?> ReadCoreAsync(CancellationToken cancellationToken)
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
        if (executable is null)
        {
            WriteFailure("HWiNFO32.exe не найден в каталоге установленных инструментов.",
                "Не найден HWiNFO32.exe в комплекте приложения.");
            return null;
        }
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
                    if (sensorProcess is not null)
                    {
                        sensorProcessStartedUtc = DateTimeOffset.UtcNow;
                        HwInfoVoltageStabilizer.Reset();
                    }
                }
            }
            ITSeti.HwInfoBridge.SensorValue[]? values = null;
            FileNotFoundException? missingMemory = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if(sensorProcess is not null && !sensorProcess.HasExited) ITSeti.HwInfoBridge.SensorReader.StartSensors(sensorProcess.Id);
                try { values = ITSeti.HwInfoBridge.SensorReader.Read(); break; }
                catch (FileNotFoundException ex)
                {
                    missingMemory = ex;
                    if (attempt < 2) await Task.Delay(250, cancellationToken);
                }
            }
            if (values is null && missingMemory is not null)
            {
                if (sensorProcess is { HasExited: false } &&
                    DateTimeOffset.UtcNow - sensorProcessStartedUtc < TimeSpan.FromSeconds(30))
                    return null;
                missingMemoryReads++;
                var startup = sensorProcess is { HasExited: false } process
                    ? ITSeti.HwInfoBridge.SensorReader.StartupState(process.Id) : "process exited";
                var details = $"Shared Memory отсутствует после трёх попыток чтения с паузой 250 мс; " +
                    $"пропуски подряд={missingMemoryReads}; {startup}; {DescribeSession(executable)}";
                if (HwInfoRecoveryPolicy.ShouldRestart(missingMemoryReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                {
                    nextRecoveryUtc = DateTimeOffset.UtcNow + HwInfoRecoveryPolicy.MinimumRestartInterval;
                    ResetSession();
                    WriteFailure("HWiNFO не создал Shared Memory; сеанс перезапущен. " + details,
                        "HWiNFO не создал Shared Memory.");
                }
                else WriteFailure("HWiNFO не создал Shared Memory. " + details,
                    "HWiNFO не создал Shared Memory.");
                return null;
            }
            missingMemoryReads = 0;
            var sourceTime = SourceTime(values ?? []);
            if (HwInfoRecoveryPolicy.IsSourceTimestampStale(sourceTime, DateTimeOffset.UtcNow))
            {
                staleSourceReads++;
                var details = $"метка источника={sourceTime:O}; возраст={(DateTimeOffset.UtcNow - sourceTime).TotalSeconds:N1} с; " +
                    $"пропуски подряд={staleSourceReads}; {DescribeSession(executable)}";
                if (HwInfoRecoveryPolicy.ShouldRestartStaleSource(staleSourceReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                {
                    nextRecoveryUtc = DateTimeOffset.UtcNow + HwInfoRecoveryPolicy.MinimumRestartInterval;
                    ResetSession();
                    WriteFailure($"HWiNFO возвращал устаревшие данные более {HwInfoRecoveryPolicy.StaleSourceReadsBeforeRestart} циклов; сеанс перезапущен. {details}",
                        "HWiNFO не обновляет Shared Memory.");
                }
                else WriteFailure("HWiNFO вернул устаревшую метку измерения; старые показания не публикуются. " + details,
                    "HWiNFO не обновляет Shared Memory.");
                return null;
            }
            staleSourceReads = 0;
            var readings = MapReadings(values ?? []);
            readings = HwInfoVoltageStabilizer.Stabilize(readings).ToArray();
            var snapshot = readings.Length == 0 ? null : new HwInfoSnapshot("HWiNFO Shared Memory", FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "",
                sourceTime, null, null, null, null, readings, "SourceTimestampChecked");
            if (snapshot is not null)
            {
                await WriteCacheAsync(snapshot, cancellationToken);
                try { File.Delete(ErrorPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                lastFailureMessage = null;
            }
            else WriteFailure("Область Shared Memory открылась, но поддерживаемых показаний нет; " + DescribeSession(executable),
                "В Shared Memory HWiNFO нет поддерживаемых датчиков.");
            return snapshot;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            if (ex is InvalidDataException)
            {
                staleSourceReads++;
                var details = $"чтение отклонено: {ex.Message}; пропуски подряд={staleSourceReads}; {DescribeSession(executable)}";
                if (HwInfoRecoveryPolicy.ShouldRestartStaleSource(staleSourceReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                {
                    nextRecoveryUtc = DateTimeOffset.UtcNow + HwInfoRecoveryPolicy.MinimumRestartInterval;
                    ResetSession();
                    WriteFailure("HWiNFO Shared Memory несколько циклов подряд была просрочена или повреждена; сеанс перезапущен. " + details,
                        "Повреждена или не обновляется Shared Memory HWiNFO.");
                }
                else WriteFailure("HWiNFO Shared Memory просрочена или повреждена. " + details,
                    "Повреждена или не обновляется Shared Memory HWiNFO.");
                return null;
            }
            WriteFailure($"{ex.GetType().FullName}: {ex.Message}; {DescribeSession(executable)}",
                "Ошибка запуска или чтения процесса HWiNFO.");
            return null;
        }
    }

    public static HwInfoSnapshot? ReadCached(TimeSpan maximumAge)
    {
        try
        {
            if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > maximumAge) return null;
            var snapshot=JsonSerializer.Deserialize<HwInfoSnapshot>(File.ReadAllText(CachePath), CacheJsonOptions);
            if (snapshot?.Sensors is null) return null;
            var valid = snapshot.Sensors.Where(sensor => sensor is not null &&
                (HwInfoSensorPolicy.Classify(sensor.Name, sensor.Unit) == sensor.Category &&
                 HwInfoSensorPolicy.IsValidValue(sensor.Category, sensor.Value) ||
                 sensor.Category == "Other" && (sensor.IsCritical || sensor.IsWarning) &&
                 Numeric.IsFinite(sensor.Value) && Math.Abs(sensor.Value) <= 1_000_000)).ToArray();
            if (valid.Length == 0) return null;
            snapshot = snapshot with { Sensors = valid };
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
                if (category is not null && HwInfoSensorPolicy.IsValidValue(category, value)) readings.Add(new(name, value, unit, category));
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

    private static void WriteFailure(string message, string summary)
    {
        if (summary == lastFailureMessage && DateTimeOffset.UtcNow < nextFailureWriteUtc) return;
        lastFailureMessage = summary;
        nextFailureWriteUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        ApplicationErrorStore.Record("HWiNFO", "Чтение датчиков", new InvalidOperationException(message), summary);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorPath)!);
            File.WriteAllText(ErrorPath, DateTimeOffset.Now.ToString("O") + Environment.NewLine + summary + Environment.NewLine + message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string DescribeSession(string executable)
    {
        string version;
        try { version = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "не определена"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { version = "ошибка чтения версии: " + ex.Message; }

        var configPath = Path.Combine(Path.GetDirectoryName(executable)!, "HWiNFO32.INI");
        var config = "ini рядом с exe отсутствует";
        try
        {
            if (File.Exists(configPath))
            {
                var keys = new HashSet<string>(["SensorsOnly", "SensorsSM", "SensorsSM2", "SensorInterval"], StringComparer.OrdinalIgnoreCase);
                var settings = File.ReadLines(configPath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && line[0] != '[')
                    .Where(line => keys.Contains(line.Split('=', 2)[0].Trim()))
                    .ToArray();
                var file = new FileInfo(configPath);
                config = $"ini={configPath}; размер={file.Length}; изменён={file.LastWriteTimeUtc:O}; " +
                    $"параметры=[{string.Join("; ", settings)}]";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { config = $"ini={configPath}; чтение завершилось ошибкой: {ex.GetType().Name}: {ex.Message}"; }

        var process = sensorProcess;
        if (process is null) return $"exe={executable}; version={version}; {config}; процесс=не запущен; " +
            $"сеанс={sessionStartedUtc:O}; следующая попытка={nextStartAttemptUtc:O}; восстановление={nextRecoveryUtc:O}";
        try
        {
            if (process.HasExited)
                return $"exe={executable}; version={version}; {config}; pid={process.Id}; процесс завершён; код={process.ExitCode}; " +
                    $"запуск={sensorProcessStartedUtc:O}; следующая попытка={nextStartAttemptUtc:O}; восстановление={nextRecoveryUtc:O}";
            var uptime = sensorProcessStartedUtc == default ? "неизвестно" : (DateTimeOffset.UtcNow - sensorProcessStartedUtc).TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) + " с";
            var title = "";
            try { title = process.MainWindowTitle; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { title = "недоступен: " + ex.Message; }
            return $"exe={executable}; version={version}; {config}; pid={process.Id}; процесс=работает; uptime={uptime}; " +
                $"окно=[{title}]; запуск={sensorProcessStartedUtc:O}; следующая попытка={nextStartAttemptUtc:O}; восстановление={nextRecoveryUtc:O}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return $"exe={executable}; version={version}; {config}; процесс недоступен: {ex.GetType().Name}: {ex.Message}; " +
                $"запуск={sensorProcessStartedUtc:O}; следующая попытка={nextStartAttemptUtc:O}; восстановление={nextRecoveryUtc:O}";
        }
    }

    private static async Task WriteCacheAsync(HwInfoSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                snapshot.Source, snapshot.ToolVersion, snapshot.Freshness, snapshot.CapturedAtUtc,
                snapshot.CpuName, snapshot.Motherboard, snapshot.BiosVersion, snapshot.MemoryType,
                snapshot.Sensors, snapshot.CpuTemperatureC, snapshot.VrmTemperatureC,
                snapshot.PchTemperatureC, snapshot.Rail12V, snapshot.Rail5V,
                snapshot.Rail3V, snapshot.CpuVoltageV, snapshot.CpuLoadPercent
            }), cancellationToken);
            File.Move(temporary, CachePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
                var sensorStatus = ReadSensorStatus(element);
                var isCritical = ReadFlag(element, "critical", "isCritical", "criticalAlarm", "isCriticalAlarm")
                    || IsCriticalStatus(sensorStatus);
                var isWarning = ReadFlag(element, "warning", "isWarning", "warningAlarm", "isWarningAlarm")
                    || IsWarningStatus(sensorStatus);
                if (category is not null && HwInfoSensorPolicy.IsValidValue(category, reading))
                    readings.Add(new(fullName, reading, unit, category, sensorStatus, isWarning, isCritical));
                else if ((isCritical || isWarning) && Numeric.IsFinite(reading) && Math.Abs(reading) <= 1_000_000)
                    readings.Add(new(fullName, reading, unit, "Other", sensorStatus, isWarning, isCritical));
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

    private static string? Classify(string name, string unit) => HwInfoSensorPolicy.Classify(name, unit);

    // Does not launch HWiNFO, alter its session or write the service cache. Used for live verification.
    public static HwInfoSnapshot? ReadSharedMemory()
    {
        var values = ITSeti.HwInfoBridge.SensorReader.Read();
        var sourceTime = SourceTime(values);
        if (HwInfoRecoveryPolicy.IsSourceTimestampStale(sourceTime, DateTimeOffset.UtcNow)) return null;
        var readings = MapReadings(values);
        if (readings.Length == 0) return null;
        var executable = FindExecutable();
        return new HwInfoSnapshot("HWiNFO Shared Memory", executable is null ? "" : FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "",
            sourceTime, null, null, null, null, readings, "SourceTimestampChecked");
    }

    private static DateTimeOffset SourceTime(ITSeti.HwInfoBridge.SensorValue[] values)
    {
        if (values.Length == 0) return DateTimeOffset.MinValue;
        try { return DateTimeOffset.FromUnixTimeSeconds(values[0].SourceUnixTime); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    internal static HwInfoSensorReading[] MapReadings(IEnumerable<ITSeti.HwInfoBridge.SensorValue> values)
    {
        var readings = new List<HwInfoSensorReading>();
        var seen = new HashSet<HwInfoSensorReading>();
        foreach (var value in values)
        {
            var category = Classify(value.Name, value.Unit);
            if (category is null || !HwInfoSensorPolicy.IsValidValue(category, value.Value)) continue;
            var reading = new HwInfoSensorReading(value.Name.Trim().TrimStart('/').Trim(), value.Value, value.Unit, category,
                IsCritical: category == "HardwareAlarm" && value.Value == 1,
                SensorId: value.SensorIndex + ":" + value.ReadingId);
            if (seen.Add(reading)) readings.Add(reading);
        }
        return readings.ToArray();
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
