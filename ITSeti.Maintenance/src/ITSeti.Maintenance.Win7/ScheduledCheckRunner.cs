using System;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace ITSeti.Maintenance.Win7
{
    internal static class ScheduledCheckRunner
    {
        internal const string TaskName = "ITSeti-Maintenance-Win7-Scheduled";
        internal static readonly string ResultsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSetiMaintenanceWin7");

        public static int RunWorker()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            try
            {
                var root = new DirectoryInfo(ResultsRoot);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Каталог результатов проверки не готов.");

                var snapshot = LegacyDiagnostics.Collect(true);
                WriteAtomically(Path.Combine(ResultsRoot, "latest.txt"), snapshot.ToReport());
                LegacyHistoryStore.Save(ResultsRoot, snapshot);
                WriteAtomically(Path.Combine(ResultsRoot, "latest.json"), snapshot.ToJson());
                var errorPath = Path.Combine(ResultsRoot, "scheduled-error.txt");
                if (File.Exists(errorPath)) File.Delete(errorPath);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(ResultsRoot, "scheduled-error.txt"), ex.ToString(), new UTF8Encoding(false)); }
                catch (Exception) { }
                return 1;
            }
        }

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
