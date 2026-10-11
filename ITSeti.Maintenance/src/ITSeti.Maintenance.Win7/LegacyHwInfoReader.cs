using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        public double? CpuVoltageV { get; set; }
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
        private static DateTime sessionStartedUtc;

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
            lock (SessionGate) sessionStartedUtc = DateTime.UtcNow;
            try { File.Delete(CachePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
            snapshot.CpuVoltageV = data.CpuVoltageV;
            if (data.CpuTemperatureC.HasValue)
            {
                snapshot.CpuTemperatureC = data.CpuTemperatureC;
                snapshot.CpuSource = "HWiNFO · служба SYSTEM";
            }
            AddTemperatureFinding(snapshot, data.VrmTemperatureC, 80, 95, "VRM");
            AddTemperatureFinding(snapshot, data.PchTemperatureC, 70, 80, "PCH");
            AddRailFinding(snapshot, data.Rail12V, 10.8, 13.2, "+12 В");
            AddRailFinding(snapshot, data.Rail5V, 4.5, 5.5, "+5 В");
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

        private static void AddRailFinding(LegacySnapshot snapshot, double? value, double min, double max, string name)
        {
            if (value.HasValue && (value.Value < min || value.Value > max))
                snapshot.Findings.Add("HWiNFO критично: напряжение " + name + " " + value.Value.ToString("N2") + " В.");
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
            var temporary = Path.Combine(Path.GetTempPath(), "ITSeti-HWiNFO-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-j\"" + temporary + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var process = new Process { StartInfo = start })
                {
                    if (!process.Start()) return ReadCached();
                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        return ReadCached();
                    }
                }
                if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0 || new FileInfo(temporary).Length > 8 * 1024 * 1024)
                    return ReadCached();
                var data = Parse(File.ReadAllText(temporary), executable);
                if (data != null) WriteCache(data);
                return data ?? ReadCached();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                return ReadCached();
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }

        private static LegacyHwInfoData ReadCached()
        {
            try
            {
                if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > CacheLifetime) return null;
                return new JavaScriptSerializer().Deserialize<LegacyHwInfoData>(File.ReadAllText(CachePath));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
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
            return result.CpuTemperatureC.HasValue || result.VrmTemperatureC.HasValue || result.PchTemperatureC.HasValue || result.Rail12V.HasValue || result.Rail5V.HasValue
                ? result : null;
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
                        if (lower.Contains("12v") || lower.Contains("+12")) result.Rail12V = value;
                        else if (lower.Contains("5v") || lower.Contains("+5")) result.Rail5V = value;
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
    }
}
