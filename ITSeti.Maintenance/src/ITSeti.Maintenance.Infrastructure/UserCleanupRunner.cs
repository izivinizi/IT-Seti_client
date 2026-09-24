using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record CleanupResult(string UserStatus, string AdminStatus)
{
    public string Summary => $"Пользователь: {UserStatus.Trim()} | Администратор: {AdminStatus.Trim()}";
}

public sealed class UserCleanupRunner
{
    private static readonly string QueueRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "CleanupRequests");
    private static readonly string ResultsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "CleanupRuns");
    private static readonly string JobsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServiceMaintenance", "UserJobs");

    public static string LatestSummary
    {
        get
        {
            try
            {
                if (!Directory.Exists(JobsRoot)) return "Очистка ещё не выполнялась";
                var root = Directory.GetDirectories(JobsRoot).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
                if (root is null) return "Очистка ещё не выполнялась";
                var resultRoot = Path.Combine(ResultsRoot, Path.GetFileName(root));
                static string? Read(string root, params string[] names) => names.Select(name => Path.Combine(root, name))
                    .Where(File.Exists).Select(File.ReadAllText).FirstOrDefault()?.Trim();
                var user = Read(root, "user-status.txt") ?? "нет результата";
                var admin = Read(resultRoot, "admin-cleanup.txt", "admin-stage.txt")
                    ?? Read(root, "admin-cleanup.txt", "admin-stage.txt") ?? "системная очистка не запускалась";
                return $"Пользователь: {user} | Система: {admin}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Статус очистки недоступен: " + ex.Message; }
        }
    }

    public async Task<CleanupResult> RunAsync(IProgress<string>? progress = null)
    {
        var backend = Path.Combine(AppContext.BaseDirectory, "Backend");
        var root = Path.Combine(JobsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(backend, "UserCleanupWorker.ps1"), Path.Combine(root, "UserCleanupWorker.ps1"));

        var installed = FullDiagnosticsRunner.IsInstalled;
        var resultRoot = Path.Combine(ResultsRoot, Path.GetFileName(root));
        using var user = StartWorker(Path.Combine(root, "UserCleanupWorker.ps1"), root, installed ? resultRoot : root);
        var userReleased = false;
        string? request = null;
        try
        {
            await WaitForFileAsync(Path.Combine(root, "ready.txt"), user, TimeSpan.FromSeconds(20));
            if (!installed)
            {
                progress?.Report("Очистка профиля без системных прав…");
                await SignalUserAsync(root, "interactive");
                userReleased = true;
                await WaitForExitAsync(user, TimeSpan.FromHours(2));
                return new CleanupResult(await ReadStatusAsync(root, "user-status.txt"), "Файлы обновлений не очищены: приложение не установлено");
            }

            request = Path.Combine(QueueRoot, Path.GetFileName(root) + ".request");
            await File.WriteAllTextAsync(request, root);
            progress?.Report("Запуск системной очистки без запроса пароля…");
            await StartInstalledTaskAsync();
            var deadline = DateTime.UtcNow.AddHours(2);
            var startupDeadline = DateTime.UtcNow.AddSeconds(60);
            var nextRetry = DateTime.UtcNow.AddSeconds(20);
            var lastStage = "";
            while (!File.Exists(Path.Combine(resultRoot, "admin-finished.txt")))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Очистка не завершилась за два часа.");
                if (DateTime.UtcNow >= startupDeadline && !Directory.Exists(resultRoot))
                    throw new InvalidOperationException("Задание очистки не начало работу за минуту. Переустановите приложение.");
                var stageFile = Path.Combine(resultRoot, "admin-stage.txt");
                if (File.Exists(stageFile))
                {
                    var stage = await File.ReadAllTextAsync(stageFile);
                    if (stage != lastStage) { progress?.Report(stage.Trim()); lastStage = stage; }
                }
                if (DateTime.UtcNow >= nextRetry && !File.Exists(Path.Combine(resultRoot, "go.txt")))
                {
                    await StartInstalledTaskAsync();
                    nextRetry = DateTime.UtcNow.AddSeconds(20);
                }
                await Task.Delay(500);
            }
            if (!File.Exists(Path.Combine(resultRoot, "go.txt")) && !File.Exists(Path.Combine(root, "go.txt")))
            {
                await SignalUserAsync(root, "abort");
                userReleased = true;
            }
            await WaitForExitAsync(user, TimeSpan.FromHours(2));
            return new CleanupResult(await ReadStatusAsync(root, "user-status.txt"),
                await ReadStatusAsync(resultRoot, "admin-cleanup.txt", "admin-stage.txt"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            progress?.Report("Системная очистка недоступна. Очищается профиль пользователя…");
            if (!userReleased && !HasSignal(root, resultRoot) && !user.HasExited)
            {
                await SignalUserAsync(root, "interactive");
                userReleased = true;
            }
            if (user.HasExited || ex is TimeoutException && userReleased)
                return new CleanupResult(await ReadStatusAsync(root, "user-status.txt"), "Файлы обновлений не очищены: " + ex.Message);
            await WaitForExitAsync(user, TimeSpan.FromHours(2));
            return new CleanupResult(await ReadStatusAsync(root, "user-status.txt"), "Файлы обновлений не очищены: " + ex.Message);
        }
        finally
        {
            if (!userReleased && !user.HasExited)
            {
                try { await SignalUserAsync(root, "abort"); } catch (IOException) { }
                try { await WaitForExitAsync(user, TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
            }
            if (request is not null) try { File.Delete(request); } catch (IOException) { }
        }
    }

    private static async Task StartInstalledTaskAsync()
    {
        using var task = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        task.StartInfo.ArgumentList.Add("/Run");
        task.StartInfo.ArgumentList.Add("/TN");
        task.StartInfo.ArgumentList.Add("ITSeti-Maintenance-Cleanup");
        task.Start();
        var output = task.StandardOutput.ReadToEndAsync();
        var errors = task.StandardError.ReadToEndAsync();
        await task.WaitForExitAsync();
        if (task.ExitCode != 0) throw new InvalidOperationException($"Задание очистки не запустилось (код {task.ExitCode}): {(await errors).Trim()} {(await output).Trim()}. Переустановите приложение.");
    }

    private static Process StartWorker(string script, string root, string signalRoot)
    {
        static string EncodePath(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var command = $"$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{EncodePath(root)}')); "
            + $"$s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{EncodePath(script)}')); "
            + $"$g=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{EncodePath(signalRoot)}')); "
            + "& ([scriptblock]::Create([IO.File]::ReadAllText($s,[Text.Encoding]::UTF8))) -JobRoot $r -SignalRoot $g";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))
        };
        return Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить штатную очистку Windows.");
    }

    private static async Task SignalUserAsync(string root, string value)
    {
        var destination = Path.Combine(root, "go.txt");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, Encoding.UTF8);
            File.Move(temporary, destination);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static bool HasSignal(string root, string resultRoot) =>
        File.Exists(Path.Combine(root, "go.txt")) || File.Exists(Path.Combine(resultRoot, "go.txt"));

    private static async Task WaitForFileAsync(string path, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            if (process.HasExited) throw new InvalidOperationException($"Пользовательская очистка не подготовлена (код {process.ExitCode}).");
            await Task.Delay(250);
        }
        throw new TimeoutException("Пользовательская очистка не подтвердила запуск.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout, IProgress<string>? progress = null, string? stageFile = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastStage = null;
        while (!process.HasExited)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Очистка не завершилась за два часа. Проверьте окно Windows и журнал задания.");
            if (stageFile is not null && File.Exists(stageFile))
            {
                try
                {
                    var stage = await File.ReadAllTextAsync(stageFile);
                    if (stage != lastStage) { progress?.Report(stage.Trim()); lastStage = stage; }
                }
                catch (IOException) { }
            }
            await Task.Delay(500);
        }
    }

    private static async Task<string> ReadStatusAsync(string root, params string[] files)
    {
        foreach (var name in files)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return (await File.ReadAllTextAsync(path)).Trim();
        }
        return "результат не получен; проверьте журнал " + root;
    }
}
