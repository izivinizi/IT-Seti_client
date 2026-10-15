using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class LegacyHwInfoData
    {
        public string Source { get; set; }
        public string ToolVersion { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public double? CpuTemperatureC { get; set; }
        public double? VrmTemperatureC { get; set; }
        public double? PchTemperatureC { get; set; }
        public double? Rail12V { get; set; }
        public double? Rail5V { get; set; }
        public double? Rail3V { get; set; }
        public double? MemoryLoadPercent { get; set; }
        public double? CpuVoltageV { get; set; }
        public double? CpuLoadPercent { get; set; }
        public List<LegacyHwInfoAlert> Alerts { get; set; } = new List<LegacyHwInfoAlert>();
        public List<LegacyHwInfoSensor> Sensors { get; set; } = new List<LegacyHwInfoSensor>();
    }

    public sealed class LegacyHwInfoSensor
    {
        public string Name { get; set; }
        public string Unit { get; set; }
        public double Value { get; set; }
        public string Category { get; set; }
    }

    public sealed class LegacyHwInfoAlert
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public string Unit { get; set; }
        public string Status { get; set; }
        public bool IsWarning { get; set; }
        public bool IsCritical { get; set; }
    }

    internal static class LegacyHwInfoReader
    {
        private static readonly Regex NumberPattern = new Regex(@"[-+]?\d+(?:[\.,]\d+)?", RegexOptions.Compiled);
        private static readonly object SessionGate = new object();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(10);
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(5);
        private const int VoltageWindowSize = 7;
        private const int MinimumVoltageSamples = 3;
        private static readonly Dictionary<string, Queue<double>> VoltageSamples = new Dictionary<string, Queue<double>>(StringComparer.OrdinalIgnoreCase);
        private static DateTime sessionStartedUtc;
        private static Process sensorProcess;
        private static DateTime nextStartAttemptUtc;
        private static DateTimeOffset nextRecoveryUtc;
        private static int missingMemoryReads;
        private static int staleSourceReads;
        private static DateTime nextErrorLogUtc;

        private static string CachePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "MaintenanceWin7", "Service", "hwinfo.json"); }
        }

        public static bool SessionRestartDue
        {
            get
            {
                lock (SessionGate)
                    return sessionStartedUtc != default(DateTime) && DateTime.UtcNow - sessionStartedUtc >= SessionLifetime;
            }
        }

        public static void ResetSession()
        {
            lock (SessionGate)
            {
                if(sensorProcess != null){try {if(!sensorProcess.HasExited) sensorProcess.Kill();}catch(InvalidOperationException){} sensorProcess.Dispose();sensorProcess=null;}
                sessionStartedUtc = default(DateTime); nextStartAttemptUtc=default(DateTime); missingMemoryReads=0; staleSourceReads=0;
            }
            ClearVoltageSamples();
            try { File.Delete(CachePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static void StopSession()
        {
            lock(SessionGate){if(sensorProcess==null)return;try{if(!sensorProcess.HasExited)sensorProcess.Kill();}catch(Exception ex)when(ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception){}sensorProcess.Dispose();sensorProcess=null;}
        }

        public static void Apply(LegacySnapshot snapshot)
        {
            if (SessionRestartDue) ResetSession();
            var data = Read();
            if (data == null) return;
            snapshot.HwInfoVersion = data.ToolVersion;
            snapshot.VrmTemperatureC = data.VrmTemperatureC;
            snapshot.PchTemperatureC = data.PchTemperatureC;
            snapshot.Rail12V = data.Rail12V;
            snapshot.Rail5V = data.Rail5V;
            snapshot.Rail3V = data.Rail3V;
            snapshot.CpuVoltageV = data.CpuVoltageV;
            if (data.CpuLoadPercent.HasValue){snapshot.CpuPercent = data.CpuLoadPercent;snapshot.CpuSource="HWiNFO · служба SYSTEM";}
            if(data.MemoryLoadPercent>=0 && data.MemoryLoadPercent<=100 && snapshot.TotalMemoryGb.HasValue)snapshot.FreeMemoryGb=snapshot.TotalMemoryGb*(100-data.MemoryLoadPercent)/100;
            if (data.CpuTemperatureC.HasValue)
            {
                snapshot.CpuTemperatureC = data.CpuTemperatureC;
                snapshot.CpuSource = "HWiNFO · служба SYSTEM";
            }
            snapshot.HwInfoCapturedAtUtc = data.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            snapshot.HwInfoAlerts = data.Alerts ?? new List<LegacyHwInfoAlert>();
            snapshot.HwInfoSensors = data.Sensors ?? new List<LegacyHwInfoSensor>();
            LegacyDiagnosticAdapter.Apply(snapshot);
        }

        private static LegacyHwInfoData Read()
        {
            if (!IsWindows()) return null;
            lock (SessionGate)
            {
                if (sessionStartedUtc == default(DateTime)) sessionStartedUtc = DateTime.UtcNow;
            }
            var executable = FindExecutable();
            if (executable == null) return ReadCached();
            try
            {
                lock(SessionGate)
                {
                    if((sensorProcess == null || sensorProcess.HasExited) && DateTime.UtcNow >= nextStartAttemptUtc)
                    {
                        nextStartAttemptUtc=DateTime.UtcNow.AddMinutes(5);
                        if(sensorProcess != null) sensorProcess.Dispose();
                        sensorProcess=Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(executable)});
                        if (sensorProcess != null) ClearVoltageSamples();
                        sessionStartedUtc=DateTime.UtcNow;
                    }
                }
                if(sensorProcess != null && !sensorProcess.HasExited) ITSeti.HwInfoBridge.SensorReader.StartSensors(sensorProcess.Id);
                var values=ITSeti.HwInfoBridge.SensorReader.Read();
                var sourceTime = DateTimeOffset.MinValue;
                try { if(values.Length != 0) sourceTime=DateTimeOffset.FromUnixTimeSeconds(values[0].SourceUnixTime); }
                catch (ArgumentOutOfRangeException) { }
                if (ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.IsSourceTimestampStale(sourceTime, DateTimeOffset.UtcNow))
                {
                    staleSourceReads++;
                    if (ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.ShouldRestartStaleSource(staleSourceReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                    {
                        nextRecoveryUtc=DateTimeOffset.UtcNow.Add(ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.MinimumRestartInterval);
                        ResetSession();
                        RecordReadError(new InvalidOperationException("HWiNFO вернул устаревшие данные; сеанс перезапущен."));
                    }
                    else RecordReadError(new InvalidOperationException("HWiNFO вернул устаревшую метку измерения; старые показания не публикуются."));
                    return ReadCached();
                }
                staleSourceReads=0;
                var data=new LegacyHwInfoData{Source="HWiNFO Shared Memory",ToolVersion=ReadFileVersion(executable),CapturedAtUtc=sourceTime.UtcDateTime};
                foreach(var value in values) ApplyCsvValue(data,value.Name,value.Unit,value.Value);
                missingMemoryReads = 0;
                StabilizeVoltages(data);
                WriteCache(data);
                return data;
            }
            catch (Exception ex) when (IsRecoverableSensorError(ex))
            {
                if (ex is FileNotFoundException)
                {
                    missingMemoryReads++;
                    if (ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.ShouldRestart(missingMemoryReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                    {
                        var state = sensorProcess != null && !sensorProcess.HasExited
                            ? ITSeti.HwInfoBridge.SensorReader.StartupState(sensorProcess.Id) : "process exited";
                        nextRecoveryUtc = DateTimeOffset.UtcNow + ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.MinimumRestartInterval;
                        ResetSession();
                        RecordReadError(new IOException("HWiNFO не создал Shared Memory; сеанс перезапущен. " + state + " " + ex.Message, ex.HResult));
                    }
                    else RecordReadError(ex);
                }
                else if (ex is InvalidDataException)
                {
                    staleSourceReads++;
                    if (ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.ShouldRestartStaleSource(staleSourceReads, DateTimeOffset.UtcNow, nextRecoveryUtc))
                    {
                        nextRecoveryUtc = DateTimeOffset.UtcNow.Add(ITSeti.Maintenance.Core.HwInfoRecoveryPolicy.MinimumRestartInterval);
                        ResetSession();
                        RecordReadError(new InvalidDataException("HWiNFO Shared Memory несколько циклов подряд была просрочена или повреждена; сеанс перезапущен.", ex));
                    }
                    else RecordReadError(ex);
                }
                else RecordReadError(ex);
                return ReadCached();
            }
        }

        private static LegacyHwInfoData ReadCached()
        {
            try
            {
                if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > CacheLifetime) return null;
                var data = new JavaScriptSerializer().Deserialize<LegacyHwInfoData>(File.ReadAllText(CachePath));
                if (data == null) return null;
                var age = DateTime.UtcNow - data.CapturedAtUtc.ToUniversalTime();
                return age >= TimeSpan.FromSeconds(-60) && age <= CacheLifetime ? data : null;
            }
            catch (Exception ex) when (IsRecoverableSensorError(ex))
            {
                RecordReadError(ex);
                return null;
            }
        }

        private static void WriteCache(LegacyHwInfoData data)
        {
            try
            {
                var directory = Path.GetDirectoryName(CachePath);
                Directory.CreateDirectory(directory);
                var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data), new System.Text.UTF8Encoding(false));
                if (File.Exists(CachePath)) File.Replace(temporary, CachePath, null); else File.Move(temporary, CachePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static LegacyHwInfoData Parse(string json, string source)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
            var result = new LegacyHwInfoData { Source = source, CapturedAtUtc = DateTime.UtcNow, ToolVersion = FindText(root, "version") ?? "" };
            Walk(root, "", result);
            return result.CpuTemperatureC.HasValue || result.VrmTemperatureC.HasValue || result.PchTemperatureC.HasValue ||
                result.Rail12V.HasValue || result.Rail5V.HasValue || result.Rail3V.HasValue ||
                result.CpuLoadPercent.HasValue || result.MemoryLoadPercent.HasValue
                ? result : null;
        }

        internal static LegacyHwInfoData ParseCsv(string csv, string source)
        {
            var lines = csv.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return null;
            var separators = new[] { ',', ';', '\t' };
            var separator = separators.OrderByDescending(value => SplitCsvLine(lines[0], value).Count).First();
            var headers = SplitCsvLine(lines[0], separator);
            var result = new LegacyHwInfoData
            {
                Source = source,
                CapturedAtUtc = DateTime.UtcNow,
                ToolVersion = ReadFileVersion(source)
            };
            foreach (var line in lines.Skip(1))
            {
                var values = SplitCsvLine(line, separator);
                for (var index = 0; index < Math.Min(headers.Count, values.Count); index++)
                {
                    var name = headers[index].Trim();
                    if (name.Length == 0 || name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Time", StringComparison.OrdinalIgnoreCase) || name.IndexOf("Timestamp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    double value;
                    if (!TryNumber(values[index], out value)) continue;
                    var unitMatch = Regex.Match(name, @"\[(?<unit>[^\]]+)\]\s*$");
                    var unit = unitMatch.Success ? unitMatch.Groups["unit"].Value : "";
                    ApplyCsvValue(result, name, unit, value);
                }
            }
            return result.CpuTemperatureC.HasValue || result.VrmTemperatureC.HasValue || result.PchTemperatureC.HasValue ||
                result.Rail12V.HasValue || result.Rail5V.HasValue || result.Rail3V.HasValue ||
                result.CpuLoadPercent.HasValue || result.MemoryLoadPercent.HasValue ? result : null;
        }

        private static void ApplyCsvValue(LegacyHwInfoData result, string name, string unit, double value)
        {
            var category = ITSeti.Maintenance.Core.HwInfoSensorPolicy.Classify(name, unit);
            if (category == null || !ITSeti.Maintenance.Core.HwInfoSensorPolicy.IsValidValue(category, value)) return;
            if (category.EndsWith("Temperature", StringComparison.Ordinal))
                result.Sensors.Add(new LegacyHwInfoSensor { Name = name, Unit = unit, Value = value, Category = category });
            switch (category)
            {
                case "CpuLoad": result.CpuLoadPercent = value; break;
                case "MemoryLoad": result.MemoryLoadPercent = value; break;
                case "CpuTemperature": result.CpuTemperatureC = Max(result.CpuTemperatureC, value); break;
                case "VrmTemperature": result.VrmTemperatureC = Max(result.VrmTemperatureC, value); break;
                case "PchTemperature": result.PchTemperatureC = Max(result.PchTemperatureC, value); break;
                case "CpuVoltage": result.CpuVoltageV = Max(result.CpuVoltageV, value); break;
                case "Rail12V": case "Rail5V": case "Rail3V": SetRail(result, category, value); break;
                case "HardwareAlarm":
                    if (value == 1) result.Alerts.Add(new LegacyHwInfoAlert { Name = name, Value = value, Unit = unit,
                        IsWarning = true, Status = ITSeti.Maintenance.Core.HwInfoSensorPolicy.DescribeAlarm(name) });
                    break;
            }
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
                else if (character == separator && !quoted) { fields.Add(value.ToString()); value.Length = 0; }
                else value.Append(character);
            }
            fields.Add(value.ToString());
            return fields;
        }

        private static bool TryNumber(string text, out double value)
        {
            value = 0;
            var match = NumberPattern.Match(text ?? "");
            return match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string ReadFileVersion(string path)
        {
            try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
            catch { return ""; }
        }

        private static void Walk(object node, string context, LegacyHwInfoData result)
        {
            var dictionary = node as IDictionary<string, object>;
            if (dictionary != null)
            {
                var label = Text(dictionary, "label", "name", "sensorName", "description", "title");
                var unit = Text(dictionary, "unit", "units") ?? "";
                var value = Number(dictionary, "value", "current", "reading", "rawValue");
                if (label != null && value.HasValue)
                {
                    var name = string.IsNullOrWhiteSpace(context) ? label : context + " / " + label;
                    var category = ITSeti.Maintenance.Core.HwInfoSensorPolicy.Classify(name, unit);
                    if (category != null && ITSeti.Maintenance.Core.HwInfoSensorPolicy.IsValidValue(category, value.Value))
                    {
                        ApplyCsvValue(result, name, unit, value.Value);
                        var sensorStatus = Text(dictionary, "status", "state", "health", "condition", "severity", "alarm", "alert");
                        var critical = Flag(dictionary, "critical", "isCritical", "criticalAlarm", "isCriticalAlarm") || IsCriticalStatus(sensorStatus);
                        var warning = Flag(dictionary, "warning", "isWarning", "warningAlarm", "isWarningAlarm") || IsWarningStatus(sensorStatus);
                        if ((critical || warning) && category != "HardwareAlarm")
                            result.Alerts.Add(new LegacyHwInfoAlert { Name = name, Value = value.Value, Unit = unit, Status = sensorStatus, IsWarning = warning, IsCritical = critical });
                    }
                }
                foreach (var item in dictionary) Walk(item.Value, string.IsNullOrWhiteSpace(context) ? item.Key : context + " / " + item.Key, result);
                return;
            }
            var values = node as IEnumerable;
            if (values == null || node is string) return;
            foreach (var item in values) Walk(item, context, result);
        }

        private static double? Max(double? current, double? value) { return !current.HasValue || value > current ? value : current; }

        private static void SetRail(LegacyHwInfoData result, string category, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var nominal = category == "Rail12V" ? 12.0 : category == "Rail5V" ? 5.0 : 3.3;
            var current = category == "Rail12V" ? result.Rail12V : category == "Rail5V" ? result.Rail5V : result.Rail3V;
            if (current.HasValue && Math.Abs(current.Value - nominal) <= Math.Abs(value - nominal)) return;
            if (category == "Rail12V") result.Rail12V = value;
            else if (category == "Rail5V") result.Rail5V = value;
            else result.Rail3V = value;
        }

        private static void StabilizeVoltages(LegacyHwInfoData data)
        {
            lock (VoltageSamples)
            {
                data.Rail12V = StabilizeRail("Rail12V", data.Rail12V);
                data.Rail5V = StabilizeRail("Rail5V", data.Rail5V);
                data.Rail3V = StabilizeRail("Rail3V", data.Rail3V);
            }
        }

        private static double? StabilizeRail(string category, double? value)
        {
            if (!value.HasValue) return null;
            Queue<double> samples;
            if (!VoltageSamples.TryGetValue(category, out samples))
            {
                samples = new Queue<double>();
                VoltageSamples.Add(category, samples);
            }
            samples.Enqueue(value.Value);
            while (samples.Count > VoltageWindowSize) samples.Dequeue();
            if (samples.Count < MinimumVoltageSamples) return null;
            var ordered = samples.OrderBy(item => item).ToArray();
            return ordered[ordered.Length / 2];
        }

        private static void ClearVoltageSamples()
        {
            lock (VoltageSamples) VoltageSamples.Clear();
        }

        private static string Text(IDictionary<string, object> values, params string[] names)
        {
            foreach (var item in values)
                if (names.Any(name => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)) && item.Value is string)
                    return Convert.ToString(item.Value);
            return null;
        }

        private static bool Flag(IDictionary<string, object> values, params string[] names)
        {
            foreach (var item in values)
                if (names.Any(name => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (item.Value is bool boolean) return boolean;
                    var text = Convert.ToString(item.Value);
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
                }
            return false;
        }

        private static bool IsCriticalStatus(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && Regex.IsMatch(status, "critical|crit|alarm|failed|failure|bad|опас|крит", RegexOptions.IgnoreCase);
        }

        private static bool IsWarningStatus(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && Regex.IsMatch(status, "warning|warn|attention|предуп|вним", RegexOptions.IgnoreCase);
        }

        private static double? Number(IDictionary<string, object> values, params string[] names)
        {
            foreach (var item in values)
                if (names.Any(name => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)))
                {
                    double number;
                    if (double.TryParse(Convert.ToString(item.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
                    var match = NumberPattern.Match(Convert.ToString(item.Value) ?? "");
                    if (match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
                }
            return null;
        }

        private static string FindText(object node, string token)
        {
            var dictionary = node as IDictionary<string, object>;
            if (dictionary != null)
            {
                foreach (var item in dictionary)
                    if (item.Key.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 && item.Value is string) return Convert.ToString(item.Value);
                foreach (var item in dictionary) { var nested = FindText(item.Value, token); if (nested != null) return nested; }
            }
            var values = node as IEnumerable;
            if (values != null && !(node is string)) foreach (var item in values) { var nested = FindText(item, token); if (nested != null) return nested; }
            return null;
        }

        private static string FindExecutable()
        {
            var root = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ITSETI_HWINFO_PATH"),
                root == null ? null : Path.Combine(root.FullName, "Tools", "HWiNFO", "HWiNFO32.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "HWiNFO", "HWiNFO32.exe"),
                @"C:\HWiNFO\HWiNFO32.exe", @"C:\HWiNFO\HWiNFOPortable.exe"
            };
            return candidates.Where(path => !string.IsNullOrWhiteSpace(path)).FirstOrDefault(File.Exists);
        }

        private static bool IsWindows() { return Environment.OSVersion.Platform == PlatformID.Win32NT; }

        private static bool IsRecoverableSensorError(Exception exception)
        {
            return !(exception is OutOfMemoryException) && !(exception is StackOverflowException) &&
                !(exception is AccessViolationException) && !(exception is ThreadAbortException);
        }

        private static void RecordReadError(Exception exception)
        {
            lock (SessionGate)
            {
                if (DateTime.UtcNow < nextErrorLogUtc) return;
                nextErrorLogUtc = DateTime.UtcNow.Add(ErrorLogInterval);
            }
            LegacyApplicationErrors.Record("HWiNFO", "Чтение датчиков", exception);
        }
    }
}
