using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    public sealed class LegacyDisk
    {
        public string Name { get; set; }
        public string PhysicalDiskModel { get; set; }
        public string MediaType { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public bool IsSystem { get; set; }
    }

    public sealed class LegacyEvent
    {
        public string Log { get; set; }
        public string Source { get; set; }
        public int Level { get; set; }
        public long Id { get; set; }
        public string Time { get; set; }
        public string Message { get; set; }
    }

    public sealed class LegacySmartDisk
    {
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string Health { get; set; }
        public string Letters { get; set; }
        public string MediaType { get; set; }
        public string TransferMode { get; set; }
        public long? PowerOnHours { get; set; }
    }

    public sealed class LegacyNetworkAdapter
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string Speed { get; set; }
        public string Addresses { get; set; }
    }

    public sealed class LegacyProcess
    {
        public string Signature { get; set; }
        public string Signer { get; set; }
        public string Name { get; set; }
        public long MemoryBytes { get; set; }
        public string Path { get; set; }
    }

    public sealed class LegacyBenchmark
    {
        public string TargetVolume { get; set; }
        public string TargetDiskModel { get; set; }
        public double? ReadMbps { get; set; }
        public double? WriteMbps { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
    }

    public sealed class LegacySnapshot
    {
        public string Id { get; set; }
        public string StartedAt { get; set; }
        public string Kind { get; set; }
        public string ComputerName { get; set; }
        public string OsName { get; set; }
        public string OsVersion { get; set; }
        public string CpuName { get; set; }
        public string GpuName { get; set; }
        public string MemoryType { get; set; }
        public string SerialNumber { get; set; }
        public string InventoryNumber { get; set; }
        public string RmsId { get; set; }
        public string AnyDeskId { get; set; }
        public List<string> AdminAccounts { get; set; } = new List<string>();
        public string CheckedAt { get; set; }
        public string LastBootAt { get; set; }
        public double? ActiveUptimeHours { get; set; }
        public double? CpuPercent { get; set; }
        public string CpuSource { get; set; }
        public double? CpuTemperatureC { get; set; }
        public double? TotalMemoryGb { get; set; }
        public double? FreeMemoryGb { get; set; }
        public double? MemoryUsedPercent { get { return TotalMemoryGb > 0 ? (TotalMemoryGb - FreeMemoryGb) / TotalMemoryGb * 100 : (double?)null; } }
        public string SmartSummary { get; set; }
        public List<LegacyDisk> Disks { get; set; } = new List<LegacyDisk>();
        public List<LegacySmartDisk> SmartDisks { get; set; } = new List<LegacySmartDisk>();
        public List<LegacyNetworkAdapter> Network { get; set; } = new List<LegacyNetworkAdapter>();
        public List<LegacyProcess> Processes { get; set; } = new List<LegacyProcess>();
        public List<LegacyEvent> Events { get; set; } = new List<LegacyEvent>();
        public LegacyBenchmark Benchmark { get; set; }
        public string CleanupSummary { get; set; }
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
                "Тип: " + (Kind ?? "Быстрая"),
                "Система: " + OsName + " (" + OsVersion + ")",
                "Серийный номер: " + (string.IsNullOrWhiteSpace(SerialNumber) ? "не получен" : SerialNumber),
                "Инв. №: " + (InventoryNumber ?? "не указан") + " | RMS: " + (RmsId ?? "не найден") + " | AnyDesk: " + (AnyDeskId ?? "не найден"),
                "Локальные администраторы: " + (AdminAccounts.Count == 0 ? "не получены" : string.Join(", ", AdminAccounts)),
                "Последний запуск: " + (LastBootAt ?? "нет данных"),
                "Наработка после запуска: " + (ActiveUptimeHours.HasValue ? ActiveUptimeHours.Value.ToString("N0") + " ч" : "нет данных"),
                "CPU: " + (CpuName ?? "не определён") + " · " + (CpuPercent.HasValue && CpuPercent.Value > 0 ? (CpuPercent.Value < 1 ? "<1%" : CpuPercent.Value.ToString("N0") + "%") : "нет данных") +
                    " · источник: " + (CpuSource ?? "старый отчёт") + " · " + (CpuTemperatureC.HasValue ? CpuTemperatureC.Value.ToString("N0") + " °C" : "температура недоступна"),
                "GPU: " + (GpuName ?? "не определена") + " | Память: " + (MemoryType ?? "тип не определён"),
                "ОЗУ: " + (TotalMemoryGb.HasValue ? string.Format("{0:N1} ГБ, свободно {1:N1} ГБ", TotalMemoryGb, FreeMemoryGb) : "нет данных"),
                "SMART: " + (SmartSummary ?? "нет данных"),
                "",
                "Диски:"
            };
            foreach (var disk in Disks)
                lines.Add(string.Format("  {0}: свободно {1:N1} из {2:N1} ГБ{3}", disk.Name,
                    disk.FreeBytes / 1073741824.0, disk.TotalBytes / 1073741824.0, disk.IsSystem ? " (системный)" : ""));
            foreach (var disk in SmartDisks)
                lines.Add("  " + disk.Model + ": " + disk.Health + " · " + disk.MediaType + " · " + disk.TransferMode +
                    (disk.PowerOnHours.HasValue ? " · " + disk.PowerOnHours.Value.ToString("N0") + " ч" : ""));
            if (Benchmark != null)
                lines.Add("Тест диска: " + (Benchmark.State == "Completed"
                    ? string.Format("чтение {0:N1}, запись {1:N1} МБ/с", Benchmark.ReadMbps, Benchmark.WriteMbps)
                    : Benchmark.Error ?? Benchmark.State));
            if (!string.IsNullOrWhiteSpace(CleanupSummary)) lines.Add("Очистка: " + CleanupSummary);
            lines.Add("Сеть: " + (Network.Count == 0 ? "нет активных адаптеров" : string.Join("; ", Network.Select(x => x.Name + " " + x.Addresses))));
            lines.Add("Процессы: " + Processes.Count);
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
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryUnbiasedInterruptTime(out ulong ticks);

        public static LegacySnapshot Collect(bool full = false, CancellationToken cancellationToken = default(CancellationToken), bool scheduled = false)
        {
            return CollectCore(full, cancellationToken, scheduled, false, true, null, null);
        }

        public static LegacySnapshot CollectWithCleanup(bool full, CancellationToken cancellationToken, bool scheduled,
            bool cleanupCurrentUser, string cleanupUserSid, IProgress<string> progress)
        {
            return CollectCore(full, cancellationToken, scheduled, true, cleanupCurrentUser, progress, cleanupUserSid);
        }

        private static LegacySnapshot CollectCore(bool full, CancellationToken cancellationToken, bool scheduled,
            bool cleanupAfterBenchmark, bool cleanupCurrentUser, IProgress<string> progress, string cleanupUserSid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new LegacySnapshot
            {
                Id = Guid.NewGuid().ToString("D"),
                StartedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ComputerName = Environment.MachineName,
                Kind = full ? "Полная" : "Быстрая",
                CheckedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
            };
            progress?.Report("Сбор сведений о Windows, памяти и администраторах");
            CollectSystem(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Измерение загрузки процессора");
            CollectCpu(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Обнаружение накопителей и разделов");
            CollectDisks(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Сбор сети и пользовательских процессов");
            LegacyExtras.Collect(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Сбор SMART через системную задачу");
            SmartTaskRunner.CollectInto(snapshot, cancellationToken, scheduled);
            cancellationToken.ThrowIfCancellationRequested();
            MapPhysicalDisks(snapshot);
            progress?.Report("Проверка журналов Windows");
            CollectEvents(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (full)
            {
                progress?.Report("Тест скорости системного диска DiskSpd");
                BenchmarkTaskRunner.CollectInto(snapshot, cancellationToken, scheduled);
            }
            else snapshot.Benchmark = new LegacyBenchmark { State = "Skipped", Error = "Быстрая проверка: тест скорости не запускался." };
            cancellationToken.ThrowIfCancellationRequested();
            if (cleanupAfterBenchmark)
            {
                progress?.Report("Очистка профиля пользователя и системных категорий Windows");
                var targetSid = cleanupUserSid ?? (cleanupCurrentUser ? UserProfileCleanupBridge.CurrentUserSid : null);
                snapshot.CleanupSummary = LegacyMaintenance.CleanupAfterFullCheck(targetSid, cancellationToken);
            }
            return snapshot;
        }

        private static void CollectSystem(LegacySnapshot result)
        {
            ulong activeTicks;
            if (QueryUnbiasedInterruptTime(out activeTicks))
            {
                result.ActiveUptimeHours = activeTicks / 36000000000.0;
                if (result.ActiveUptimeHours >= 60)
                    result.Findings.Add("Компьютер проработал " + result.ActiveUptimeHours.Value.ToString("N0") +
                        " ч без перезагрузки (без сна и гибернации). Сохраните документы и перезагрузите его.");
            }
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

            try
            {
                using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT * FROM Win32_Group WHERE SID='S-1-5-32-544'"))
                using (var groups = query.Get())
                {
                    var group = groups.Cast<ManagementObject>().FirstOrDefault();
                    if (group == null) throw new InvalidOperationException("Локальная группа администраторов не найдена.");
                    using (var members = group.GetRelated("Win32_Account"))
                    {
                        foreach (ManagementObject member in members)
                        {
                            var name = Convert.ToString(member["Name"]);
                            var domain = Convert.ToString(member["Domain"]);
                            if (!string.IsNullOrWhiteSpace(name))
                                result.AdminAccounts.Add((string.IsNullOrWhiteSpace(domain) ? "" : domain + "\\") + name);
                        }
                    }
                }
                result.AdminAccounts = result.AdminAccounts.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) { result.Unavailable.Add("Локальные администраторы: " + ex.Message); }
        }

        internal static string[] FindAdministratorCandidates()
        {
            var accounts = new List<string>();
            using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT * FROM Win32_Group WHERE SID='S-1-5-32-544' AND LocalAccount=True"))
            {
                query.Options.Timeout = TimeSpan.FromSeconds(5);
                using (var groups = query.Get())
                foreach (ManagementObject group in groups)
                using (group)
                using (var members = group.GetRelated("Win32_UserAccount"))
                foreach (ManagementObject member in members)
                using (member)
                {
                    if (Convert.ToBoolean(member["Disabled"])) continue;
                    var name = Convert.ToString(member["Name"]);
                    var domain = Convert.ToString(member["Domain"]);
                    if (!string.IsNullOrWhiteSpace(name))
                        accounts.Add((string.IsNullOrWhiteSpace(domain) ? "" : domain + "\\") + name);
                }
            }
            return accounts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void CollectCpu(LegacySnapshot result)
        {
            string source;
            result.CpuPercent = CpuLoadSampler.Read(out source);
            result.CpuSource = source;
            if (!result.CpuPercent.HasValue)
                result.Unavailable.Add("Загрузка CPU: счётчики Windows и замер процессов не дали достоверного значения.");
            else if (result.CpuPercent >= 85)
                result.Findings.Add("Высокая загрузка процессора; повторите замер при обычной работе.");
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

        private static void MapPhysicalDisks(LegacySnapshot result)
        {
            try
            {
                using (var query = new ManagementObjectSearcher("root\\cimv2", "SELECT Model,MediaType,DeviceID,SerialNumber FROM Win32_DiskDrive"))
                using (var drives = query.Get())
                {
                    foreach (ManagementObject drive in drives)
                    {
                        var model = Convert.ToString(drive["Model"]);
                        if (string.IsNullOrWhiteSpace(model)) continue;
                        var media = Convert.ToString(drive["MediaType"]);
                        var letters = new List<string>();
                        using (var partitions = drive.GetRelated("Win32_DiskPartition"))
                        {
                            foreach (ManagementObject partition in partitions)
                            using (var logicalDisks = partition.GetRelated("Win32_LogicalDisk"))
                            {
                                foreach (ManagementObject logical in logicalDisks)
                                {
                                    var letter = Convert.ToString(logical["DeviceID"]);
                                    if (!string.IsNullOrWhiteSpace(letter)) letters.Add(letter.TrimEnd('\\') + "\\");
                                }
                            }
                        }
                        var matching = result.Disks.Where(x => letters.Any(letter =>
                            string.Equals(x.Name.TrimEnd('\\') + "\\", letter, StringComparison.OrdinalIgnoreCase))).ToArray();
                        foreach (var volume in matching)
                        {
                            volume.PhysicalDiskModel = model.Trim();
                            volume.MediaType = Regex.IsMatch(model + " " + media, "SSD|Solid State|NVMe", RegexOptions.IgnoreCase)
                                ? "SSD" : Regex.IsMatch(media ?? "", "Fixed hard disk", RegexOptions.IgnoreCase) ? "HDD" : null;
                        }
                        var serial = NormalizeDiskSerial(Convert.ToString(drive["SerialNumber"]));
                        var smart = !string.IsNullOrEmpty(serial)
                            ? result.SmartDisks.FirstOrDefault(x => NormalizeDiskSerial(x.SerialNumber) == serial)
                            : null;
                        if (smart == null)
                        {
                            var sameModel = result.SmartDisks.Where(x => NormalizeDiskModel(x.Model) == NormalizeDiskModel(model)).ToArray();
                            var matchingDriveCount = result.SmartDisks.Count(x => NormalizeDiskModel(x.Model) == NormalizeDiskModel(model));
                            if (sameModel.Length == 1 && matchingDriveCount == 1) smart = sameModel[0];
                        }
                        if (smart != null && letters.Count > 0) smart.Letters = string.Join(", ", letters.Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException || ex is UnauthorizedAccessException || ex is COMException)
            {
                result.Unavailable.Add("Сопоставление томов с физическими дисками: " + ex.Message);
            }
        }

        private static string NormalizeDiskSerial(string value) => Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();
        private static string NormalizeDiskModel(string value) => Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();

        internal static void CollectSmart(LegacySnapshot result, Func<bool> cancellationRequested = null)
        {
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "CrystalDiskInfo");
            var exe = Path.Combine(source, Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe");
            if (!File.Exists(exe)) { result.Unavailable.Add("SMART: CrystalDiskInfo отсутствует в комплекте."); return; }
            var temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSetiMaintenanceWin7", "work-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (cancellationRequested != null && cancellationRequested()) throw new OperationCanceledException();
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
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!process.WaitForExit(500) && DateTime.UtcNow < deadline)
                    {
                        if (cancellationRequested != null && cancellationRequested())
                        {
                            try { process.Kill(); } catch (InvalidOperationException) { }
                            try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
                            throw new OperationCanceledException();
                        }
                    }
                    if (!process.HasExited)
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
                    var serial = Regex.Match(block, @"(?im)^\s*Serial Number\s*:\s*(.*)$").Groups[1].Value.Trim();
                    var letters = Regex.Match(block, @"(?m)^\s*Drive Letter\s*:\s*(.*)$").Groups[1].Value.Trim();
                    var transfer = Regex.Match(block, @"(?m)^\s*Transfer Mode\s*:\s*(.*)$").Groups[1].Value.Trim();
                    var rotation = Regex.Match(block, @"(?m)^\s*Rotation Rate\s*:\s*(.*)$").Groups[1].Value.Trim();
                    var interfaceName = Regex.Match(block, @"(?m)^\s*Interface\s*:\s*(.*)$").Groups[1].Value.Trim();
                    var hoursText = Regex.Match(block, @"(?im)^\s*Power On Hours?\s*:\s*([\d\s,.]+)").Groups[1].Value;
                    long hours;
                    var powerOnHours = long.TryParse(Regex.Replace(hoursText, @"\D", ""), out hours) ? (long?)hours : null;
                    var mediaType = Regex.IsMatch(rotation + interfaceName, "SSD|Solid State|NVMe|NVM Express", RegexOptions.IgnoreCase)
                        ? "SSD" : Regex.IsMatch(rotation, "RPM", RegexOptions.IgnoreCase) ? "HDD" : "Не определён";
                    result.SmartDisks.Add(new LegacySmartDisk { Model = disk, SerialNumber = serial, Health = state, Letters = letters,
                        MediaType = mediaType, TransferMode = transfer, PowerOnHours = powerOnHours });
                    disks.Add(disk + ": " + state);
                    if (Regex.IsMatch(state, "Caution|Bad|Тревога|Плохо", RegexOptions.IgnoreCase))
                        result.Findings.Add("SMART: " + disk + " — " + state + ".");
                    if (powerOnHours > 60000)
                        result.Findings.Add("Наработка диска " + disk + " превысила 60 000 ч.");
                }
                if (disks.Count == 0) throw new InvalidDataException("Формат отчёта SMART не распознан.");
                result.SmartSummary = string.Join("; ", disks);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                result.Unavailable.Add("SMART: CrystalDiskInfo требует права администратора; проверка пропущена без запроса пароля.");
            }
            catch (OperationCanceledException) { throw; }
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

        private static void CollectEvents(LegacySnapshot result, CancellationToken cancellationToken)
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
                            cancellationToken.ThrowIfCancellationRequested();
                            var entry = log.Entries[i];
                            if (entry.TimeGenerated < cutoff) break;
                            count++;
                            string message;
                            try { message = Regex.Replace(entry.Message ?? "", @"\s+", " ").Trim(); }
                            catch (Exception) { message = "Текст события недоступен."; }
                            if (message.Length > 180) message = message.Substring(0, 180) + "...";
                            result.Events.Add(new LegacyEvent
                            {
                                Log = name, Source = entry.Source, Id = entry.InstanceId & 0xffff,
                                Level = entry.EntryType == EventLogEntryType.Error || entry.EntryType == EventLogEntryType.FailureAudit ? 2
                                    : entry.EntryType == EventLogEntryType.Warning ? 3 : 4,
                                Time = entry.TimeGenerated.ToString("yyyy-MM-dd HH:mm"), Message = message
                            });
                        }
                        if (count == 300) result.Unavailable.Add("Журнал " + name + ": просмотрены только последние 300 событий.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Unavailable.Add("Журнал " + name + ": " + ex.Message); }
            }
            result.Events = result.Events.OrderByDescending(x => x.Time).Take(100).ToList();
        }
    }
}
