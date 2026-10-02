using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    public sealed class LegacyDisk
    {
        public string Name { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public bool IsSystem { get; set; }
    }

    public sealed class LegacyEvent
    {
        public string Log { get; set; }
        public string Source { get; set; }
        public long Id { get; set; }
        public string Time { get; set; }
        public string Message { get; set; }
    }

    public sealed class LegacySnapshot
    {
        public string ComputerName { get; set; }
        public string OsName { get; set; }
        public string OsVersion { get; set; }
        public string SerialNumber { get; set; }
        public string CheckedAt { get; set; }
        public string LastBootAt { get; set; }
        public double? CpuPercent { get; set; }
        public double? TotalMemoryGb { get; set; }
        public double? FreeMemoryGb { get; set; }
        public string SmartSummary { get; set; }
        public List<LegacyDisk> Disks { get; set; } = new List<LegacyDisk>();
        public List<LegacyEvent> Events { get; set; } = new List<LegacyEvent>();
        public List<string> Findings { get; set; } = new List<string>();
        public List<string> Unavailable { get; set; } = new List<string>();

        public string ToJson() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(this); }

        public string ToReport()
        {
            var lines = new List<string>
            {
                "ИТ-Сети | Диагностика Windows 7 (beta)",
                "Компьютер: " + ComputerName,
                "Проверено: " + CheckedAt,
                "Система: " + OsName + " (" + OsVersion + ")",
                "Серийный номер: " + (string.IsNullOrWhiteSpace(SerialNumber) ? "не получен" : SerialNumber),
                "Последний запуск: " + (LastBootAt ?? "нет данных"),
                "CPU: " + (CpuPercent.HasValue ? CpuPercent.Value.ToString("N0") + "%" : "нет данных"),
                "ОЗУ: " + (TotalMemoryGb.HasValue ? string.Format("{0:N1} ГБ, свободно {1:N1} ГБ", TotalMemoryGb, FreeMemoryGb) : "нет данных"),
                "SMART: " + (SmartSummary ?? "нет данных"),
                "",
                "Диски:"
            };
            foreach (var disk in Disks)
                lines.Add(string.Format("  {0}: свободно {1:N1} из {2:N1} ГБ{3}", disk.Name,
                    disk.FreeBytes / 1073741824.0, disk.TotalBytes / 1073741824.0, disk.IsSystem ? " (системный)" : ""));
            lines.Add("");
            lines.Add("Требует внимания:");
            lines.AddRange(Findings.Count == 0 ? new[] { "  По доступным данным замечаний нет." } : Findings.Select(x => "  - " + x));
            lines.Add("");
            lines.Add("Не удалось проверить:");
            lines.AddRange(Unavailable.Count == 0 ? new[] { "  Нет." } : Unavailable.Select(x => "  - " + x));
            lines.Add("");
            lines.Add("Последние ошибки Windows:");
            lines.AddRange(Events.Count == 0 ? new[] { "  Не найдены или журнал недоступен." } :
                Events.Take(20).Select(x => string.Format("  {0} {1} | {2} #{3}: {4}", x.Time, x.Log, x.Source, x.Id, x.Message)));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static class LegacyDiagnostics
    {
        public static LegacySnapshot Collect()
        {
            var snapshot = new LegacySnapshot
            {
                ComputerName = Environment.MachineName,
                CheckedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
            };
            CollectSystem(snapshot);
            CollectCpu(snapshot);
            CollectDisks(snapshot);
            SmartTaskRunner.CollectInto(snapshot);
            CollectEvents(snapshot);
            return snapshot;
        }

        private static void CollectSystem(LegacySnapshot result)
        {
            try
            {
                using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT Caption,Version,ServicePackMajorVersion,LastBootUpTime,TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (var values = query.Get())
                {
                    var os = values.Cast<ManagementObject>().FirstOrDefault();
                    if (os == null) throw new InvalidOperationException("WMI не вернул сведения об ОС.");
                    result.OsName = Convert.ToString(os["Caption"]);
                    result.OsVersion = Convert.ToString(os["Version"]);
                    var boot = Convert.ToString(os["LastBootUpTime"]);
                    if (!string.IsNullOrEmpty(boot)) result.LastBootAt = ManagementDateTimeConverter.ToDateTime(boot).ToString("dd.MM.yyyy HH:mm");
                    double totalKb, freeKb;
                    if (double.TryParse(Convert.ToString(os["TotalVisibleMemorySize"]), out totalKb) &&
                        double.TryParse(Convert.ToString(os["FreePhysicalMemory"]), out freeKb))
                    {
                        result.TotalMemoryGb = totalKb / 1048576.0;
                        result.FreeMemoryGb = freeKb / 1048576.0;
                        if (totalKb > 0 && (freeKb / totalKb < .1 || freeKb < 1048576))
                            result.Findings.Add("Мало свободной оперативной памяти.");
                    }
                }
            }
            catch (Exception ex) { result.Unavailable.Add("ОС и ОЗУ: " + ex.Message); }

            try
            {
                using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT SerialNumber FROM Win32_BIOS"))
                using (var values = query.Get())
                {
                    var bios = values.Cast<ManagementObject>().FirstOrDefault();
                    var serial = bios == null ? null : Convert.ToString(bios["SerialNumber"]);
                    if (!string.IsNullOrWhiteSpace(serial) && !Regex.IsMatch(serial, "^(To Be Filled|Default String|None|System Serial)", RegexOptions.IgnoreCase))
                        result.SerialNumber = serial.Trim();
                }
            }
            catch (Exception ex) { result.Unavailable.Add("Серийный номер: " + ex.Message); }
        }

        private static void CollectCpu(LegacySnapshot result)
        {
            try
            {
                var samples = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                        using (var values = query.Get())
                        {
                            var cpu = values.Cast<ManagementObject>().FirstOrDefault();
                            double value;
                            if (cpu != null && double.TryParse(Convert.ToString(cpu["PercentProcessorTime"]), out value) && value >= 0 && value <= 100)
                                samples.Add(value);
                        }
                    }
                    catch (ManagementException) { break; }
                    if (i < 2) Thread.Sleep(600);
                }
                if (samples.Count == 0)
                {
                    using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT LoadPercentage FROM Win32_Processor"))
                    using (var values = query.Get())
                        foreach (ManagementObject processor in values)
                        {
                            double value;
                            if (double.TryParse(Convert.ToString(processor["LoadPercentage"]), out value) && value >= 0 && value <= 100)
                                samples.Add(value);
                        }
                }
                if (samples.Count == 0) throw new InvalidOperationException("Счётчики CPU не вернули значение.");
                samples.Sort();
                result.CpuPercent = samples[samples.Count / 2];
                if (result.CpuPercent >= 85) result.Findings.Add("Высокая загрузка процессора; повторите замер при обычной работе.");
            }
            catch (Exception ex) { result.Unavailable.Add("Загрузка CPU: " + ex.Message); }
        }

        private static void CollectDisks(LegacySnapshot result)
        {
            try
            {
                using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
                using (var values = query.Get())
                {
                    foreach (ManagementObject volume in values)
                    {
                        var total = Convert.ToInt64(volume["Size"] ?? 0);
                        var free = Convert.ToInt64(volume["FreeSpace"] ?? 0);
                        var name = Convert.ToString(volume["DeviceID"]);
                        var isSystem = string.Equals(name, Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        result.Disks.Add(new LegacyDisk { Name = name, TotalBytes = total, FreeBytes = free, IsSystem = isSystem });
                        if (total >= 50L * 1073741824 || isSystem)
                            if (free < 5L * 1073741824 || (total > 0 && free < total / 10))
                                result.Findings.Add("Мало свободного места на " + name + ".");
                    }
                }
                result.Disks = result.Disks.OrderByDescending(x => x.IsSystem).ThenBy(x => x.Name).ToList();
                if (result.Disks.Count == 0) result.Unavailable.Add("Локальные диски не найдены.");
            }
            catch (Exception ex) { result.Unavailable.Add("Диски: " + ex.Message); }
        }

        internal static void CollectSmart(LegacySnapshot result)
        {
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "CrystalDiskInfo");
            var exe = Path.Combine(source, Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe");
            if (!File.Exists(exe)) { result.Unavailable.Add("SMART: CrystalDiskInfo отсутствует в комплекте."); return; }
            var temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSetiMaintenanceWin7", "work-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(source, temporary);
                var localExe = Path.Combine(temporary, Path.GetFileName(exe));
                using (var process = Process.Start(new ProcessStartInfo(localExe, "/CopyExit")
                {
                    WorkingDirectory = temporary,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Утилита не запустилась.");
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new TimeoutException("CrystalDiskInfo не завершил экспорт за 30 секунд.");
                    }
                    if (process.ExitCode != 0) throw new InvalidOperationException("CrystalDiskInfo завершился с кодом " + process.ExitCode + ".");
                }
                var report = Path.Combine(temporary, "DiskInfo.txt");
                if (!File.Exists(report)) throw new FileNotFoundException("CrystalDiskInfo не создал отчёт SMART.");
                var text = File.ReadAllText(report);
                var blocks = Regex.Split(text, @"(?m)(?=^\s*Model\s*:)");
                var disks = new List<string>();
                foreach (var block in blocks)
                {
                    var model = Regex.Match(block, @"(?m)^\s*Model\s*:\s*(.+)$");
                    var health = Regex.Match(block, @"(?m)^\s*Health Status\s*:\s*(.+)$");
                    if (!model.Success || !health.Success) continue;
                    var state = health.Groups[1].Value.Trim();
                    var disk = model.Groups[1].Value.Trim();
                    disks.Add(disk + ": " + state);
                    if (Regex.IsMatch(state, "Caution|Bad|Тревога|Плохо", RegexOptions.IgnoreCase))
                        result.Findings.Add("SMART: " + disk + " — " + state + ".");
                }
                if (disks.Count == 0) throw new InvalidDataException("Формат отчёта SMART не распознан.");
                result.SmartSummary = string.Join("; ", disks);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                result.Unavailable.Add("SMART: CrystalDiskInfo требует права администратора; проверка пропущена без запроса пароля.");
            }
            catch (Exception ex) { result.Unavailable.Add("SMART: " + ex.Message); }
            finally
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        private static void CollectEvents(LegacySnapshot result)
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var name in new[] { "System", "Application" })
            {
                try
                {
                    using (var log = new EventLog(name))
                    {
                        var count = 0;
                        for (var i = log.Entries.Count - 1; i >= 0 && count < 300; i--)
                        {
                            var entry = log.Entries[i];
                            if (entry.TimeGenerated < cutoff) break;
                            count++;
                            if (entry.EntryType != EventLogEntryType.Error && entry.EntryType != EventLogEntryType.FailureAudit) continue;
                            string message;
                            try { message = Regex.Replace(entry.Message ?? "", @"\s+", " ").Trim(); }
                            catch (Exception) { message = "Текст события недоступен."; }
                            if (message.Length > 180) message = message.Substring(0, 180) + "...";
                            result.Events.Add(new LegacyEvent
                            {
                                Log = name, Source = entry.Source, Id = entry.InstanceId & 0xffff,
                                Time = entry.TimeGenerated.ToString("yyyy-MM-dd HH:mm"), Message = message
                            });
                        }
                        if (count == 300) result.Unavailable.Add("Журнал " + name + ": просмотрены только последние 300 событий.");
                    }
                }
                catch (Exception ex) { result.Unavailable.Add("Журнал " + name + ": " + ex.Message); }
            }
            result.Events = result.Events.OrderByDescending(x => x.Time).Take(20).ToList();
        }
    }
}
