using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ITSeti.Maintenance.Win7
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--collect-smart")
            {
                try { return SmartTaskRunner.RunWorker(); }
                catch { return 1; }
            }
            if (args.Length == 1 && args[0] == "--collect-benchmark")
            {
                try { return BenchmarkTaskRunner.RunWorker(); }
                catch { return 1; }
            }
            if (args.Length == 1 && args[0] == "--scheduled-check")
            {
                try { return ScheduledCheckRunner.RunWorker(); }
                catch { return 1; }
            }
            if (args.Length == 1 && (args[0] == "--register-smart-task" || args[0] == "--unregister-smart-task"))
            {
                try
                {
                    if (args[0] == "--register-smart-task") SmartTaskInstaller.Register();
                    else SmartTaskInstaller.Unregister();
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SmartTaskSetup.error.txt"), ex.ToString(), new UTF8Encoding(false));
                    return 1;
                }
            }
            if (args.Length == 2 && args[0] == "--diagnose")
            {
                try
                {
                    var snapshot = LegacyDiagnostics.Collect();
                    File.WriteAllText(args[1], snapshot.ToJson(), new UTF8Encoding(false));
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[1] + ".error", ex.ToString(), new UTF8Encoding(false));
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var engineer = args.Length == 1 && args[0] == "--engineer" &&
                new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Application.Run(new MainForm(engineer));
            return 0;
        }
    }
}
