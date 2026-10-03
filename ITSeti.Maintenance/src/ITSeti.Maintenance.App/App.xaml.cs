using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class App : Application
{
    private bool interactiveWindowShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--cpu-temperature-probe", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            var outputIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--cpu-temperature-probe", StringComparison.OrdinalIgnoreCase)) + 1;
            if (outputIndex >= e.Args.Length) { Shutdown(2); return; }
            string? output = null;
            try
            {
                output = Path.GetFullPath(e.Args[outputIndex]);
                var reading = (await CpuTemperatureReader.ReadAsync()) with { CapturedAtUtc = DateTimeOffset.UtcNow };
                await WriteTemperatureResultAsync(output, reading);
                Shutdown();
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    try
                    {
                        var failure = new CpuTemperatureReading(null,
                            $"Ошибка опроса датчика: {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}", DateTimeOffset.UtcNow);
                        await WriteTemperatureResultAsync(output, failure);
                    }
                    catch { }
                }
                Shutdown(1);
            }
            return;
        }

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

                    var retry = new ScheduledCheckPrompt().ShowDialog() == true;
                    await File.WriteAllTextAsync(reminderPath, DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
                    if (retry) await RunScheduledFullRetryAsync();
                    Shutdown();
                    return;
                }
                Shutdown();
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
                interactiveWindowShown = true;
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

    private static async Task WriteTemperatureResultAsync(string output, CpuTemperatureReading reading)
    {
        var directory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Не задана папка результата датчика", nameof(output));
        Directory.CreateDirectory(directory);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(reading));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, output, true);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100 * (attempt + 1));
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { }
        }
    }

    private static string? GetPendingAutoFullMaintenance(string? systemDataOverride = null)
    {
        var data = systemDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        var pending = Path.Combine(data, "pending-auto-full-maintenance.txt");
        if (!File.Exists(pending)) return null;
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
                catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException) { }
            }
        }
        base.OnExit(e);
    }

    private static async Task RunScheduledFullRetryAsync()
    {
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
