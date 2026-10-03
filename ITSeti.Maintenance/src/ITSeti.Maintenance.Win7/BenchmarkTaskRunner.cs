using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace ITSeti.Maintenance.Win7
{
    internal static class BenchmarkTaskRunner
    {
        internal const string TaskName = "ITSeti-Maintenance-Win7-Benchmark";
        internal const string ScheduledTaskName = "ITSeti-Maintenance-Win7-Scheduled-Benchmark";
        private static readonly string ResultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSetiMaintenanceWin7", "benchmark.json");
        private static string ResultFor(bool scheduled) => scheduled
            ? Path.Combine(ScheduledCheckRunner.ResultsRoot, "benchmark-scheduled.json") : ResultPath;

        public static int RunWorker(bool scheduled = false)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            var result = new LegacyBenchmark();
            try
            {
                var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                var workRoot = Path.GetDirectoryName(ResultPath);
                if (!string.Equals(Path.GetPathRoot(workRoot), systemRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Каталог теста находится не на системном диске.");
                var drive = new DriveInfo(systemRoot);
                if (drive.AvailableFreeSpace < 3L * 1073741824)
                    throw new InvalidOperationException("Для теста системного диска нужно минимум 3 ГБ свободного места.");
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "DiskSpd",
                    Environment.Is64BitOperatingSystem ? "DiskSpd64L.exe" : "DiskSpd32L.exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("Win7-версия DiskSpd не найдена.", exe);
                var target = Path.Combine(workRoot, "benchmark-" + Guid.NewGuid().ToString("N") + ".dat");
                try
                {
                    var reads = new double[2];
                    var writes = new double[2];
                    for (var pass = 0; pass < 2; pass++)
                    {
                        Func<bool> cancelled = scheduled ? (Func<bool>)null : () => ManualTaskCancellation.IsRequested("benchmark");
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        reads[pass] = Measure(exe, target, false, cancelled);
                        writes[pass] = Measure(exe, target, true, cancelled);
                    }
                    result.ReadMbps = Math.Round(reads.Average(), 1);
                    result.WriteMbps = Math.Round(writes.Average(), 1);
                    result.State = "Completed";
                }
                finally
                {
                    if (File.Exists(target)) File.Delete(target);
                }
            }
            catch (OperationCanceledException) when (!scheduled) { ManualTaskCancellation.MarkStopped("benchmark"); return 2; }
            catch (Exception ex)
            {
                result.State = "Failed";
                result.Error = ex.Message;
            }
            var destination = ResultFor(scheduled);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result.State == "Completed" ? 0 : 1;
        }

        private static double Measure(string exe, string target, bool write, Func<bool> cancelled)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var arguments = "-c1G -b1M -t1 -o1 -s " + (write ? "-w100" : "-w0") +
                " -h -W1 -d6 -C0 -Rxml \"" + target + "\"";
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(exe, arguments)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (error) error.AppendLine(args.Data); };
                if (!process.Start()) throw new InvalidOperationException("DiskSpd не запустился.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!process.WaitForExit(500) && DateTime.UtcNow < deadline)
                {
                    if (cancelled != null && cancelled())
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
                        throw new OperationCanceledException();
                    }
                }
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("DiskSpd превысил 90 секунд на одном проходе.");
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("DiskSpd завершился с кодом " + process.ExitCode + ": " + error.ToString().Trim());
            }
            var text = output.ToString();
            var start = text.IndexOf("<Results", StringComparison.Ordinal);
            var end = text.LastIndexOf("</Results>", StringComparison.Ordinal);
            if (start < 0 || end < start) throw new InvalidDataException("DiskSpd не вернул XML-отчёт.");
            var xml = XDocument.Parse(text.Substring(start, end + "</Results>".Length - start));
            var span = xml.Descendants("TimeSpan").FirstOrDefault(x => x.Element("TestTimeSeconds") != null);
            if (span == null) throw new InvalidDataException("В XML DiskSpd нет результата.");
            double seconds;
            if (!double.TryParse((string)span.Element("TestTimeSeconds"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                throw new InvalidDataException("DiskSpd не вернул длительность теста.");
            var metric = write ? "WriteBytes" : "ReadBytes";
            var bytes = span.Descendants("Thread").Descendants("Target").Sum(x => (double?)x.Element(metric) ?? 0);
            if (bytes <= 0) throw new InvalidDataException("DiskSpd не вернул объём " + (write ? "записи" : "чтения") + ".");
            return bytes / seconds / 1000000;
        }

        public static void CollectInto(LegacySnapshot snapshot, CancellationToken cancellationToken = default(CancellationToken), bool scheduled = false)
        {
            try
            {
                if (!scheduled) ManualTaskCancellation.Begin("benchmark");
                var started = DateTime.UtcNow;
                var taskName = scheduled ? ScheduledTaskName : TaskName;
                var resultPath = ResultFor(scheduled);
                using (var process = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    "/Run /TN \"" + taskName + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (process == null) throw new InvalidOperationException("Не удалось запустить установленную задачу DiskSpd.");
                    var schedulerDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (!process.WaitForExit(300) && DateTime.UtcNow < schedulerDeadline)
                        ThrowIfCancelled(cancellationToken, scheduled);
                    if (!process.HasExited || process.ExitCode != 0)
                        throw new InvalidOperationException("Не удалось запустить установленную задачу DiskSpd.");
                }
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < deadline)
                {
                    ThrowIfCancelled(cancellationToken, scheduled);
                    if (File.Exists(resultPath) && File.GetLastWriteTimeUtc(resultPath) >= started)
                    {
                        snapshot.Benchmark = new JavaScriptSerializer().Deserialize<LegacyBenchmark>(File.ReadAllText(resultPath));
                        if (snapshot.Benchmark == null) throw new InvalidDataException("DiskSpd вернул пустой отчёт.");
                        if (snapshot.Benchmark.State != "Completed") snapshot.Unavailable.Add("Тест скорости: " + snapshot.Benchmark.Error);
                        else
                        {
                            var system = snapshot.Disks.FirstOrDefault(x => x.IsSystem);
                            var smart = system == null ? null : snapshot.SmartDisks.FirstOrDefault(x =>
                                !string.IsNullOrWhiteSpace(x.Letters) && x.Letters.IndexOf(system.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (smart != null && snapshot.Benchmark.ReadMbps.HasValue)
                            {
                                var nvme = (smart.TransferMode ?? "").IndexOf("PCIe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    (smart.TransferMode ?? "").IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0;
                                var limit = nvme ? 900 : smart.MediaType == "HDD" ? 100 : smart.MediaType == "SSD" ? 210 : 0;
                                if (limit > 0 && snapshot.Benchmark.ReadMbps.Value < limit)
                                    snapshot.Findings.Add("Скорость системного диска ниже ориентира: чтение " +
                                        snapshot.Benchmark.ReadMbps.Value.ToString("N0") + " МБ/с (ориентир " + limit + " МБ/с).");
                            }
                        }
                        return;
                    }
                    Thread.Sleep(500);
                }
                throw new TimeoutException("Задача DiskSpd не вернула отчёт за 5 минут.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                snapshot.Benchmark = new LegacyBenchmark { State = "Failed", Error = ex.Message };
                snapshot.Unavailable.Add("Тест скорости: " + ex.Message);
            }
        }

        private static void ThrowIfCancelled(CancellationToken token, bool scheduled)
        {
            if (!token.IsCancellationRequested) return;
            if (!scheduled) { ManualTaskCancellation.Cancel("benchmark"); ManualTaskCancellation.WaitStopped("benchmark"); }
            token.ThrowIfCancellationRequested();
        }
    }
}
