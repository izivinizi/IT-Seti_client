using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--cpu-temperature-probe", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            var outputIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--cpu-temperature-probe", StringComparison.OrdinalIgnoreCase)) + 1;
            if (outputIndex >= e.Args.Length) { Shutdown(2); return; }
            try
            {
                var reading = (await CpuTemperatureReader.ReadAsync()) with { CapturedAtUtc = DateTimeOffset.UtcNow };
                var output = Path.GetFullPath(e.Args[outputIndex]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(reading));
                File.Move(temporary, output, true);
                Shutdown();
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

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
                var reminderTask = e.Args.Contains("--reminder-task", StringComparer.OrdinalIgnoreCase);
                if (reminderTask) DeleteScheduledReminder();
                if (!reminderTask && !IsScheduledCheckDue(data))
                {
                    Shutdown();
                    return;
                }

                if (new ScheduledCheckPrompt().ShowDialog() == true)
                {
                    var snoozePath = Path.Combine(data, "scheduled-reminder-snooze.txt");
                    try { File.Delete(snoozePath); } catch (IOException) { }
                    var window = new MainWindow();
                    MainWindow = window;
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    window.Show();
                    await window.RunScheduledQuickCheckAsync();
                    return;
                }

                var until = DateTimeOffset.UtcNow.AddDays(1);
                await File.WriteAllTextAsync(Path.Combine(data, "scheduled-reminder-snooze.txt"), until.ToString("O"));
                if (!ScheduleReminder(until))
                    MessageBox.Show("Напоминание отложено на сутки. Если компьютер будет выключен в это время, оно появится при следующем входе в Windows.",
                        "Плановая проверка", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private static bool IsScheduledCheckDue(string userData)
    {
        var systemData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        var lastPath = Path.Combine(systemData, "last-quick-run.txt");
        if (File.Exists(lastPath) && DateTimeOffset.TryParse(File.ReadAllText(lastPath), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var lastRun) && DateTimeOffset.UtcNow - lastRun < TimeSpan.FromDays(14))
            return false;

        var snoozePath = Path.Combine(userData, "scheduled-reminder-snooze.txt");
        return !File.Exists(snoozePath) || !DateTimeOffset.TryParse(File.ReadAllText(snoozePath), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var snoozedUntil) || DateTimeOffset.UtcNow >= snoozedUntil;
    }

    private static string ReminderTaskName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value?.Replace("-", "", StringComparison.Ordinal) ?? Environment.UserName;
            return "ITSeti-Maintenance-Reminder-" + sid;
        }
    }

    private static bool ScheduleReminder(DateTimeOffset due)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "/Create", "/TN", ReminderTaskName, "/SC", "ONCE",
                    "/SD", due.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
                    "/ST", due.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    "/TR", $"\"{executable}\" --scheduled-quick --reminder-task", "/F", "/IT"
                }
            });
            if (process is null || !process.WaitForExit(10000))
            {
                try { process?.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteScheduledReminder()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/Delete", "/TN", ReminderTaskName, "/F" }
            });
            if (process is not null && !process.WaitForExit(5000)) process.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) { }
    }
}
