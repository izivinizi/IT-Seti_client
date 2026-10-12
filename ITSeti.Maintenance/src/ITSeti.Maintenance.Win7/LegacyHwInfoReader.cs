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
    }

    internal sealed class LegacyHwInfoAlert
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
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(10);
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(5);
        private const int VoltageWindowSize = 7;
        private const int MinimumVoltageSamples = 3;
        private static readonly Dictionary<string, Queue<double>> VoltageSamples = new Dictionary<string, Queue<double>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex Rail12Pattern = new Regex(@"(?<![a-z0-9])\+?\s*12(?:[.,]0{1,2})?\s*v(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Rail5Pattern = new Regex(@"(?<![a-z0-9])\+?\s*5(?:[.,]0{1,2})?\s*v(?:s\s*b)?(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Rail3Pattern = new Regex(@"(?<![a-z0-9])\+?\s*3[.,]3\s*v(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static DateTime sessionStartedUtc;
        private static Process sensorProcess;
        private static DateTime nextStartAttemptUtc;
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
                sessionStartedUtc = default(DateTime); nextStartAttemptUtc=default(DateTime);
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
            AddTemperatureFinding(snapshot, data.CpuTemperatureC, 75, 85, "CPU");
            AddTemperatureFinding(snapshot, data.VrmTemperatureC, 80, 95, "VRM");
            AddTemperatureFinding(snapshot, data.PchTemperatureC, 70, 80, "PCH");
            AddRailFinding(snapshot, data.Rail12V, 11.4, 12.6, 10.8, 13.2, "+12 В");
            AddRailFinding(snapshot, data.Rail5V, 4.75, 5.25, 4.5, 5.5, "+5 В");
            AddRailFinding(snapshot, data.Rail3V, 3.135, 3.465, 2.97, 3.63, "+3,3 В");
            foreach (var alert in data.Alerts ?? new List<LegacyHwInfoAlert>())
            {
                var level = alert.IsCritical ? "критично" : "предупреждение";
                snapshot.Findings.Add("HWiNFO " + level + ": " + alert.Name + " " + alert.Value.ToString("N2") + " " + (alert.Unit ?? "") +
                    (string.IsNullOrWhiteSpace(alert.Status) ? "" : " (" + alert.Status + ")."));
            }
        }

        private static void AddTemperatureFinding(LegacySnapshot snapshot, double? value, double warning, double critical, string name)
        {
            if (!value.HasValue) return;
            if (value.Value >= critical) snapshot.Findings.Add("HWiNFO критично: " + name + " " + value.Value.ToString("N0") + " °C.");
            else if (value.Value >= warning) snapshot.Findings.Add("HWiNFO предупреждение: " + name + " " + value.Value.ToString("N0") + " °C.");
        }

        private static void AddRailFinding(LegacySnapshot snapshot, double? value, double warningMin, double warningMax,
            double criticalMin, double criticalMax, string name)
        {
            if (!value.HasValue) return;
            if (value.Value < criticalMin || value.Value > criticalMax)
                snapshot.Findings.Add("HWiNFO критично: напряжение " + name + " " + value.Value.ToString("N2") + " В.");
            else if (value.Value < warningMin || value.Value > warningMax)
                snapshot.Findings.Add("HWiNFO предупреждение: напряжение " + name + " " + value.Value.ToString("N2") + " В.");
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
                var data=new LegacyHwInfoData{Source="HWiNFO Shared Memory",ToolVersion=ReadFileVersion(executable),CapturedAtUtc=DateTime.UtcNow};
                foreach(var value in values) ApplyCsvValue(data,value.Name,value.Unit,value.Value);
                StabilizeVoltages(data);
                WriteCache(data);
                return data;
            }
            catch (Exception ex) when (IsRecoverableSensorError(ex))
            {
                RecordReadError(ex);
                return ReadCached();
            }
        }

        private static LegacyHwInfoData ReadCached()
        {
            try
            {
                if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > CacheLifetime) return null;
                return new JavaScriptSerializer().Deserialize<LegacyHwInfoData>(File.ReadAllText(CachePath));
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
            var lower = name.ToLowerInvariant();
            if(lower.Contains("distance to") || lower.Contains("tjmax") || lower.Contains("thermal limit"))return;
            if(value>0 && !unit.Contains("C") && (lower.Contains("thermal throttling") || lower.Contains("thermal trip") || lower.Contains("critical temperature")))
                result.Alerts.Add(new LegacyHwInfoAlert{Name=name,Value=value,Unit=unit,IsCritical=true});
            if(unit.Contains("%") && lower.Contains("physical memory load")){result.MemoryLoadPercent=value;return;}
            if (unit.Contains("%") && (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("процессор")) &&
                (lower.Contains("load") || lower.Contains("usage") || lower.Contains("util") || lower.Contains("загруз") || lower.Contains("использ")))
            {if(lower.Contains("total cpu usage") || !result.CpuLoadPercent.HasValue)result.CpuLoadPercent=value;}
            else if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp") || lower.Contains("температур")) &&
                (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop"))) result.VrmTemperatureC = Max(result.VrmTemperatureC, value);
            else if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp") || lower.Contains("температур")) &&
                (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller") || lower.Contains("чипсет"))) result.PchTemperatureC = Max(result.PchTemperatureC, value);
            else if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp") || lower.Contains("температур")) &&
                (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie") || lower.Contains("процессор"))) result.CpuTemperatureC = Max(result.CpuTemperatureC, value);
            else if (unit.Trim().Equals("V", StringComparison.OrdinalIgnoreCase) || lower.Contains("voltage") || lower.Contains(" vcore"))
            {
                if (Rail3Pattern.IsMatch(lower)) SetRail(result, "Rail3V", value);
                else if (Rail12Pattern.IsMatch(lower)) SetRail(result, "Rail12V", value);
                else if (Rail5Pattern.IsMatch(lower)) SetRail(result, "Rail5V", value);
                else if (lower.Contains("vcore") || lower.Contains("vsoc") || lower.Contains("vsc") || lower.Contains("cpu core")) result.CpuVoltageV = value;
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
                    var lower = name.ToLowerInvariant();
                    var sensorStatus = Text(dictionary, "status", "state", "health", "condition", "severity", "alarm", "alert");
                    var isCritical = Flag(dictionary, "critical", "isCritical", "criticalAlarm", "isCriticalAlarm") || IsCriticalStatus(sensorStatus);
                    var isWarning = Flag(dictionary, "warning", "isWarning", "warningAlarm", "isWarningAlarm") || IsWarningStatus(sensorStatus);
                    if (isCritical || isWarning)
                        result.Alerts.Add(new LegacyHwInfoAlert { Name = name, Value = value.Value, Unit = unit, Status = sensorStatus, IsWarning = isWarning, IsCritical = isCritical });
                    if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp")) &&
                        (lower.Contains("cpu") || lower.Contains("processor") || lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie")))
                        result.CpuTemperatureC = Max(result.CpuTemperatureC, value);
                    else if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp")) &&
                        (lower.Contains("vrm") || lower.Contains("mos") || lower.Contains("vr loop")))
                        result.VrmTemperatureC = Max(result.VrmTemperatureC, value);
                    else if ((unit.IndexOf("c", StringComparison.OrdinalIgnoreCase) >= 0 || lower.Contains("temperature") || lower.Contains("temp")) &&
                        (lower.Contains("pch") || lower.Contains("chipset") || lower.Contains("southbridge") || lower.Contains("platform controller")))
                        result.PchTemperatureC = Max(result.PchTemperatureC, value);
                    else if (unit.Trim().Equals("V", StringComparison.OrdinalIgnoreCase) || lower.Contains("voltage") || lower.Contains(" vcore"))
                    {
                        if (Rail3Pattern.IsMatch(lower)) SetRail(result, "Rail3V", value.Value);
                        else if (Rail12Pattern.IsMatch(lower)) SetRail(result, "Rail12V", value.Value);
                        else if (Rail5Pattern.IsMatch(lower)) SetRail(result, "Rail5V", value.Value);
                        else if (lower.Contains("vcore") || lower.Contains("vcc") || lower.Contains("vsoc") || lower.Contains("vsc") || lower.Contains("cpu core")) result.CpuVoltageV = value;
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
