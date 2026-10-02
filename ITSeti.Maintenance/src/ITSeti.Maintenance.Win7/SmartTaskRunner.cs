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
        private static readonly string ResultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSetiMaintenanceWin7", "smart.json");

        public static int RunWorker()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            var snapshot = new LegacySnapshot();
            LegacyDiagnostics.CollectSmart(snapshot);
            var result = new SmartTaskResult
            {
                SmartSummary = snapshot.SmartSummary,
                SmartDisks = snapshot.SmartDisks,
                Findings = snapshot.Findings,
                Unavailable = snapshot.Unavailable
            };
            var folder = Path.GetDirectoryName(ResultPath);
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, "smart-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                if (File.Exists(ResultPath)) File.Replace(temporary, ResultPath, null);
                else File.Move(temporary, ResultPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return snapshot.Unavailable.Count == 0 ? 0 : 1;
        }

        public static void CollectInto(LegacySnapshot snapshot)
        {
            try
            {
                var started = DateTime.UtcNow;
                using (var process = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    "/Run /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Планировщик не запустился.");
                    if (!process.WaitForExit(10000)) throw new TimeoutException("Планировщик не ответил за 10 секунд.");
                    if (process.ExitCode != 0) throw new InvalidOperationException("Задача SMART не запущена (код " + process.ExitCode + "). Проверьте установку приложения.");
                }

                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(ResultPath) && File.GetLastWriteTimeUtc(ResultPath) >= started)
                    {
                        SmartTaskResult result;
                        using (var stream = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
            catch (Exception ex)
            {
                snapshot.Unavailable.Add("SMART: " + ex.Message);
            }
        }
    }
}
