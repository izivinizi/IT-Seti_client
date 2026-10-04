using System;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace ITSeti.Maintenance.Win7
{
    internal static class ScheduledCheckRunner
    {
        internal const string TaskName = "ITSeti-Maintenance-Win7-Scheduled";
        internal const string FullTaskName = "ITSeti-Maintenance-Win7-AutoFull";
        internal static readonly string ResultsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSetiMaintenanceWin7");

        public static int RunWorker() { return RunWorker(false); }
        public static int RunFullWorker() { return RunWorker(true); }

        private static int RunWorker(bool full)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            try
            {
                var root = new DirectoryInfo(ResultsRoot);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Каталог результатов проверки не готов.");
                if (full && !IsFullDue(DateTime.UtcNow, ResultsRoot)) return 0;

                var snapshot = LegacyDiagnostics.Collect(full, scheduled: true);
                var errorPath = Path.Combine(ResultsRoot, "scheduled-error.txt");
                if (File.Exists(errorPath)) File.Delete(errorPath);
                if (full)
                {
                    try
                    {
                        var repair = LegacyMaintenance.RunSfc();
                        var succeeded = repair.IndexOf("код 0", StringComparison.OrdinalIgnoreCase) >= 0;
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-status.txt"),
                            (succeeded ? "Завершена: " : "Ошибка восстановления: ") + repair);
                        if (succeeded)
                        {
                            SetFullBaseline(DateTime.UtcNow);
                            DeleteIfExists(Path.Combine(ResultsRoot, "scheduled-full-failure.txt"));
                            DeleteIfExists(Path.Combine(ResultsRoot, "scheduled-full-remind-after.txt"));
                        }
                        else WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-failure.txt"), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex)
                    {
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-status.txt"), "Ошибка восстановления: " + ex.Message);
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-failure.txt"), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    try
                    {
                        var cleanup = LegacyMaintenance.CleanupAfterFullCheck(
                            UserProfileCleanupBridge.ActiveConsoleUserSid(), System.Threading.CancellationToken.None);
                        snapshot.CleanupSummary = cleanup;
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-cleanup.txt"), cleanup);
                    }
                    catch (Exception ex)
                    {
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-cleanup.txt"), "Очистка не выполнена: " + ex.Message);
                    }
                }
                WriteAtomically(Path.Combine(ResultsRoot, "latest.txt"), snapshot.ToReport());
                LegacyHistoryStore.Save(ResultsRoot, snapshot);
                WriteAtomically(Path.Combine(ResultsRoot, "latest.json"), snapshot.ToJson());
                ServerReportUploader.Queue(snapshot);
                ServerReportUploader.Trigger();
                QueueApplicationUpdate();
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(ResultsRoot, "scheduled-error.txt"), ex.ToString(), new UTF8Encoding(false)); }
                catch (Exception) { }
                if (full)
                {
                    try
                    {
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-status.txt"), "Ошибка полной проверки: " + ex.Message);
                        WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-failure.txt"), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch (Exception) { }
                }
                try { ServerReportUploader.Trigger(); } catch (Exception) { }
                try { QueueApplicationUpdate(); } catch (Exception) { }
                return 1;
            }
        }

        internal static bool IsFullDue(DateTime utcNow, string root)
        {
            if (File.Exists(Path.Combine(root,"scheduled-full-failure.txt"))) return true;
            DateTime baseline;
            var path = Path.Combine(root,"full-check-baseline.txt");
            if (!File.Exists(path) || !DateTime.TryParse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,out baseline)) return false;
            return utcNow.ToUniversalTime() >= baseline.ToUniversalTime().AddDays(60);
        }

        internal static void SetFullBaseline(DateTime utcNow)
        {
            WriteAtomically(Path.Combine(ResultsRoot,"full-check-baseline.txt"),utcNow.ToUniversalTime().ToString("o",System.Globalization.CultureInfo.InvariantCulture));
        }

        public static bool NeedsDaytimeFullFailurePrompt(DateTime localNow)
        {
            return NeedsDaytimeFullFailurePrompt(localNow,
                Path.Combine(ResultsRoot, "scheduled-full-failure.txt"),
                Path.Combine(ResultsRoot, "scheduled-full-remind-after.txt"));
        }

        internal static bool NeedsDaytimeFullFailurePrompt(DateTime localNow, string failurePath, string remindPath)
        {
            if (localNow.Hour < 8 || localNow.Hour >= 21) return false;
            if (!File.Exists(failurePath)) return false;
            DateTime failedUtc;
            if (!DateTime.TryParse(File.ReadAllText(failurePath).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out failedUtc)) return false;
            var failedLocal = failedUtc.ToLocalTime();
            if (failedLocal.Date != localNow.Date || localNow < failedLocal.AddHours(2)) return false;
            DateTime remindUtc;
            return !File.Exists(remindPath) || !DateTime.TryParse(File.ReadAllText(remindPath).Trim(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out remindUtc) ||
                DateTime.UtcNow >= remindUtc.ToUniversalTime();
        }

        public static void RemindTomorrow()
        {
            WriteAtomically(Path.Combine(ResultsRoot, "scheduled-full-remind-after.txt"),
                DateTime.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }

        public static bool StartFullRun()
        {
            using (var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                "/Run /TN \"" + FullTaskName + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
                return process != null && process.WaitForExit(10000) && process.ExitCode == 0;
        }

        private static void QueueApplicationUpdate()
        {
            using (var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                "/Run /TN \"ITSeti-Maintenance-Win7-Update\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
                if (process == null || !process.WaitForExit(10000) || process.ExitCode != 0)
                    throw new InvalidOperationException("Задача фонового обновления не запустилась.");
        }

        private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }

        private static void WriteAtomically(string destination, string value)
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value, new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
