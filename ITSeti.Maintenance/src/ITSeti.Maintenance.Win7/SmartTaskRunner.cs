using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class SmartTaskResult
    {
        public string SmartSummary { get; set; }
        public List<LegacySmartDisk> SmartDisks { get; set; }
        public List<string> Findings { get; set; }
        public List<string> Unavailable { get; set; }
    }

    internal static class SmartTaskRunner
    {
        internal const string TaskName = "ITSeti-Maintenance-Win7-SMART";
        internal const string ScheduledTaskName = "ITSeti-Maintenance-Win7-Scheduled-SMART";
        private static readonly string ResultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSetiMaintenanceWin7", "smart.json");
        private static string ResultFor(bool scheduled) => scheduled
            ? Path.Combine(ScheduledCheckRunner.ResultsRoot, "smart-scheduled.json") : ResultPath;

        public static int RunWorker(bool scheduled = false)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            var snapshot = new LegacySnapshot();
            try { LegacyDiagnostics.CollectSmart(snapshot, scheduled ? (Func<bool>)null : () => ManualTaskCancellation.IsRequested("smart")); }
            catch (OperationCanceledException) when (!scheduled) { ManualTaskCancellation.MarkStopped("smart"); return 2; }
            var result = new SmartTaskResult
            {
                SmartSummary = snapshot.SmartSummary,
                SmartDisks = snapshot.SmartDisks,
                Findings = snapshot.Findings,
                Unavailable = snapshot.Unavailable
            };
            var folder = Path.GetDirectoryName(ResultPath);
            Directory.CreateDirectory(folder);
            var destination = ResultFor(scheduled);
            var temporary = Path.Combine(folder, "smart-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return snapshot.Unavailable.Count == 0 ? 0 : 1;
        }

        public static void CollectInto(LegacySnapshot snapshot, CancellationToken cancellationToken = default(CancellationToken), bool scheduled = false)
        {
            try
            {
                if (!scheduled) ManualTaskCancellation.Begin("smart");
                var started = DateTime.UtcNow;
                var taskName = scheduled ? ScheduledTaskName : TaskName;
                var resultPath = ResultFor(scheduled);
                using (var process = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    "/Run /TN \"" + taskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Планировщик не запустился.");
                    var schedulerDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (!process.WaitForExit(300) && DateTime.UtcNow < schedulerDeadline)
                        ThrowIfCancelled(cancellationToken, scheduled);
                    if (!process.HasExited) throw new TimeoutException("Планировщик не ответил за 10 секунд.");
                    if (process.ExitCode != 0) throw new InvalidOperationException("Задача SMART не запущена (код " + process.ExitCode + "). Проверьте установку приложения.");
                }

                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    ThrowIfCancelled(cancellationToken, scheduled);
                    if (File.Exists(resultPath) && File.GetLastWriteTimeUtc(resultPath) >= started)
                    {
                        SmartTaskResult result;
                        using (var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            result = new JavaScriptSerializer().Deserialize<SmartTaskResult>(reader.ReadToEnd());
                        if (result == null) throw new InvalidDataException("Задача SMART вернула пустой отчёт.");
                        snapshot.SmartSummary = result.SmartSummary;
                        if (result.SmartDisks != null) snapshot.SmartDisks.AddRange(result.SmartDisks);
                        if (result.Findings != null) snapshot.Findings.AddRange(result.Findings);
                        if (result.Unavailable != null) snapshot.Unavailable.AddRange(result.Unavailable);
                        return;
                    }
                    Thread.Sleep(300);
                }
                throw new TimeoutException("Задача SMART не вернула отчёт за 45 секунд.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                snapshot.Unavailable.Add("SMART: " + ex.Message);
            }
        }

        private static void ThrowIfCancelled(CancellationToken token, bool scheduled)
        {
            if (!token.IsCancellationRequested) return;
            if (!scheduled) { ManualTaskCancellation.Cancel("smart"); ManualTaskCancellation.WaitStopped("smart"); }
            token.ThrowIfCancellationRequested();
        }
    }
}
