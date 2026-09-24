using System.Windows;
using System.IO;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--scheduled-quick", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance");
            Directory.CreateDirectory(data);
            using var mutex = new Mutex(false, @"Local\ITSeti-Maintenance-Quick-" + Environment.UserName);
            if (!mutex.WaitOne(0)) { Shutdown(); return; }
            try
            {
                var history = new SqliteHistoryStore(Path.Combine(data, "history.db"));
                DiagnosticSnapshot? snapshot = null;
                if (FullDiagnosticsRunner.IsInstalled)
                {
                    var reports = await FullDiagnosticsRunner.ReadInstalledReportsAsync();
                    await history.SaveManyAsync(reports);
                    snapshot = reports.OrderByDescending(report => report.StartedAt).FirstOrDefault();
                    var seenPath = Path.Combine(data, "last-notified-system-report.txt");
                    var seenId = File.Exists(seenPath) ? (await File.ReadAllTextAsync(seenPath)).Trim() : "";
                    if (snapshot is null || snapshot.Id.ToString() == seenId || DiagnosticRules.GetUserIssues(snapshot).Count == 0)
                    {
                        if (snapshot is not null && snapshot.Id.ToString() != seenId)
                            await File.WriteAllTextAsync(seenPath, snapshot.Id.ToString());
                        Shutdown();
                        return;
                    }
                    await File.WriteAllTextAsync(seenPath, snapshot.Id.ToString());
                }
                else
                {
                    var stamp = Path.Combine(data, "last-scheduled-quick.txt");
                    if (File.Exists(stamp) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(stamp), out var last)
                        && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(14)) { Shutdown(); return; }
                    snapshot = await new FullDiagnosticsRunner().RunQuickAsync(new Progress<DiagnosticProgress>(_ => { }));
                    await history.SaveAsync(snapshot);
                    await File.WriteAllTextAsync(stamp, DateTimeOffset.UtcNow.ToString("O"));
                    if (DiagnosticRules.GetUserIssues(snapshot).Count == 0) { Shutdown(); return; }
                }
                MainWindow = new MainWindow();
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindow.Show();
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(data, "scheduled-quick-error.txt"), ex.ToString());
                Shutdown();
            }
            finally { mutex.ReleaseMutex(); }
            return;
        }
        if (MainWindow is null)
        {
            try
            {
                MainWindow = new MainWindow();
                MainWindow.Show();
            }
            catch (Exception ex)
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance");
                try { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "startup-error.txt"), ex.ToString()); } catch (IOException) { }
                MessageBox.Show($"Не удалось открыть приложение: {ex.Message}", "ИТ-Сети", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }
    }
}
