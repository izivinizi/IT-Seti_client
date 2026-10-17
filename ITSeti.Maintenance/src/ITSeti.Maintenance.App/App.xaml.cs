using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class App : Application
{
    private bool interactiveWindowShown;

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var entry = ApplicationErrorStore.Record("Приложение", "Необработанная ошибка интерфейса", args.Exception);
            MessageBox.Show(ApplicationErrorStore.UserMessage(entry), "Ошибка приложения", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ApplicationErrorStore.Record("Приложение", "Критическая ошибка процесса", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ApplicationErrorStore.Record("Приложение", "Ошибка фоновой операции", args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--engineer-bootstrap", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                EngineerWindowLauncher.HandleBootstrap(e.Args);
                Shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Shutdown(1223);
            }
            catch (Exception ex)
            {
                ApplicationErrorStore.Record("Режим инженера", "Запуск инженерского окна", ex);
                MessageBox.Show("Не удалось открыть инженерское окно: " + ex.Message,
                    "Режим инженера", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Contains("--engineer-window", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (!EngineerWindowLauncher.IsAdministrator())
            {
                MessageBox.Show("Инженерское окно не запущено с правами администратора.",
                    "Режим инженера", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            var database = EngineerWindowLauncher.GetArgument(e.Args, "--history-db")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "history.db");
            MainWindow = new MainWindow(database, engineerOnly: true,
                EngineerWindowLauncher.GetArgument(e.Args, "--client-sid"),
                EngineerWindowLauncher.GetArgument(e.Args, "--client-local-data"));
            MainWindow.Show();
            interactiveWindowShown = true;
            return;
        }
        if (e.Args.Contains("--scheduled-quick", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance");
            Directory.CreateDirectory(data);
            using var mutex = new Mutex(false, @"Local\ITSeti-Maintenance-Quick-" + Environment.UserName);
            if (!mutex.WaitOne(0)) { Shutdown(); return; }
            try
            {
                var pendingFull = GetPendingAutoFullMaintenance();
                if (pendingFull is not null)
                {
                    var reminderPath = Path.Combine(data, "full-maintenance-remind-after.txt");
                    try
                    {
                        if (DateTimeOffset.TryParse(await File.ReadAllTextAsync(reminderPath), out var remindAfter)
                            && remindAfter > DateTimeOffset.UtcNow)
                        {
                            Shutdown();
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

                    var retry = new ScheduledCheckPrompt(pendingFull=="overdue").ShowDialog() == true;
                    await File.WriteAllTextAsync(reminderPath, DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
                    if (retry) await RunScheduledFullRetryAsync();
                    Shutdown();
                    return;
                }
                Shutdown();
            }
            catch (Exception ex)
            {
                ApplicationErrorStore.Record("Расписание","Дневное предложение плановой проверки",ex);
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
                interactiveWindowShown = true;
            }
            catch (Exception ex)
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance");
                var error = ApplicationErrorStore.Record("Приложение", "Запуск главного окна", ex);
                try
                {
                    Directory.CreateDirectory(data);
                    File.WriteAllText(Path.Combine(data, "startup-error.txt"), error.Summary + Environment.NewLine + error.TechnicalDetails);
                }
                catch (Exception logException) when (logException is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                MessageBox.Show($"Не удалось открыть приложение: {ex.Message}", "ИТ-Сети", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }
    }

    private static string? GetPendingAutoFullMaintenance(string? systemDataOverride = null)
    {
        var data = systemDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        var pending = Path.Combine(data, "pending-auto-full-maintenance.txt");
        if (!File.Exists(pending))
        {
            var baseline=Path.Combine(data,"last-full-run.txt");
            return File.Exists(baseline) && DateTimeOffset.TryParse(File.ReadAllText(baseline),out var last) && DateTimeOffset.UtcNow-last.ToUniversalTime()>=TimeSpan.FromDays(10) && DateTime.Now.Hour>=8 && DateTime.Now.Hour<21 ? "overdue" : null;
        }
        var runId = File.ReadAllText(pending).Trim();
        if (!Guid.TryParseExact(runId, "N", out _)) return null;
        return File.Exists(Path.Combine(data, "Runs", runId, "error.txt")) ? runId : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (interactiveWindowShown && ApplicationUpdateRunner.IsPendingInstall(ApplicationUpdateRunner.ReadRawStatus()))
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "Backend", "Resume-ApplicationUpdate.ps1");
            if (File.Exists(helper))
            {
                try
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                    };
                    foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper,
                        "-ParentPid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(argument);
                    Process.Start(start)?.Dispose();
                }
                catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
                { ApplicationErrorStore.Record("Обновление", "Продолжение установки после закрытия окна", exception); }
            }
        }
        base.OnExit(e);
    }

    private static async Task RunScheduledFullRetryAsync()
    {
        var service = await MaintenanceServiceClient.StartAsync(MaintenanceServiceOperation.ScheduledFullCheck);
        if (service.Connected)
        {
            if (!service.Accepted) throw new InvalidOperationException(service.Message);
            return;
        }
        using var process = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/Run", "/TN", "ITSeti-Maintenance-AutoFullRepair" }
        } };
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила повторную плановую проверку.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException("Планировщик не запустил повторную проверку (код " + process.ExitCode + ").");
    }

}
