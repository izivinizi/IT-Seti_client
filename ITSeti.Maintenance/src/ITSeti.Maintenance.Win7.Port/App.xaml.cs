using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Windows;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args;
            if (args.Length > 0)
            {
                try
                {
                    var result = HandleWorker(args);
                    if (result.HasValue) { Shutdown(result.Value); return; }
                }
                catch (Exception ex)
                {
                    var registration = args.Length == 1 && (args[0] == "--register-smart-task" || args[0] == "--unregister-smart-task");
                    var errorPath = registration
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SmartTaskSetup.error.txt")
                        : Path.Combine(Path.GetTempPath(), "ITSeti-Win7Worker.error.txt");
                    try { File.WriteAllText(errorPath, ex.ToString(), new UTF8Encoding(false)); }
                    catch (IOException) { }
                    Shutdown(1);
                    return;
                }
            }
            var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            var preview = args.Length == 2 && args[0] == "--preview-report" ? args[1] : null;
            var cleanupSid = ReadOption(args, "--cleanup-sid");
#if DEBUG
            if (args.Length >= 2 && args[0] == "--preview-admin") preview = args[1];
#endif
            var engineer = elevated && args.Contains("--engineer");
#if DEBUG
            engineer |= args.Length >= 2 && args[0] == "--preview-admin";
#endif
            var window = new MainWindow(engineer, preview, cleanupSid);
#if DEBUG
            if (engineer && preview != null && args.Length == 3)
            {
                int tab;
                if (int.TryParse(args[2], out tab) && tab >= 0 && tab < window.AdminTabs.Items.Count)
                {
                    window.AdminNavigation.SelectedIndex = tab;
                    window.AdminTabs.SelectedIndex = tab;
                }
            }
#endif
            MainWindow = window;
            window.Show();
        }

        private static int? HandleWorker(string[] args)
        {
            if (args.Length > 0 && args[0] == "--engineer-bootstrap")
            {
                EngineerLauncher.Bootstrap(ReadOption(args, "--cleanup-sid"));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--collect-smart") return SmartTaskRunner.RunWorker();
            if (args.Length == 1 && args[0] == "--collect-smart-scheduled") return SmartTaskRunner.RunWorker(scheduled: true);
            if (args.Length == 1 && args[0] == "--collect-benchmark") return BenchmarkTaskRunner.RunWorker();
            if (args.Length == 1 && args[0] == "--collect-benchmark-scheduled") return BenchmarkTaskRunner.RunWorker(scheduled: true);
            if (args.Length == 1 && args[0] == "--cleanup-system") return SystemCleanupTaskRunner.RunWorker();
            if (args.Length == 1 && args[0] == "--scheduled-check") return ScheduledCheckRunner.RunWorker();
            if (args.Length == 1 && args[0] == "--scheduled-full") return ScheduledCheckRunner.RunFullWorker();
            if (args.Length == 1 && args[0] == "--upload-reports") return ServerReportUploader.Upload();
            if (args.Length == 1 && args[0] == "--update-application") return Win7GitHubUpdater.RunBackgroundWorker();
            if (args.Length == 1 && args[0] == "--register-smart-task") { SmartTaskInstaller.Register(); return 0; }
            if (args.Length == 1 && args[0] == "--unregister-smart-task") { SmartTaskInstaller.Unregister(); return 0; }
            if (args.Length == 2 && args[0] == "--diagnose")
            {
                File.WriteAllText(args[1], LegacyDiagnostics.Collect().ToJson(), new UTF8Encoding(false));
                return 0;
            }
            return null;
        }

        private static string ReadOption(string[] args, string option)
        {
            for (var index = 0; index < args.Length - 1; index++)
                if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }
    }
}
