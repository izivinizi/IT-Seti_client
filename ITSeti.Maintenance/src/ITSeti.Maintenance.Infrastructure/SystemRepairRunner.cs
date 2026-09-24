using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class SystemRepairRunner
{
    private static readonly string InstalledRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "Repairs");
    private static readonly string LocalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "Repairs");

    public static string LatestStatus
    {
        get
        {
            var root = ReadLatestRoot(InstalledRoot) ?? ReadLatestRoot(LocalRoot);
            if (root is null) return "Восстановление отдельно не запускалось";
            return ReadStatus(root) ?? "Запуск восстановления…";
        }
    }

    public static string? LatestLogPath
    {
        get
        {
            var root = ReadLatestRoot(InstalledRoot) ?? ReadLatestRoot(LocalRoot);
            var path = root is null ? null : Path.Combine(root, "repair.log");
            return path is not null && File.Exists(path) ? path : null;
        }
    }

    public async Task<string> RunAsync(IProgress<string>? progress = null)
    {
        var root = FullDiagnosticsRunner.IsInstalled
            ? await StartInstalledAsync(progress)
            : StartDirect(progress);
        var deadline = DateTime.UtcNow.AddHours(3);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = ReadStatus(root);
            if (status is not null && status != last) { progress?.Report(status); last = status; }
            if (File.Exists(Path.Combine(root, "finished.txt"))) return status ?? "Восстановление завершилось без итогового статуса";
            await Task.Delay(1000);
        }
        throw new TimeoutException("Восстановление не завершилось за три часа. Журнал: " + root);
    }

    private static async Task<string> StartInstalledAsync(IProgress<string>? progress)
    {
        var previous = ReadLatestRoot(InstalledRoot);
        using var task = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        task.StartInfo.ArgumentList.Add("/Run");
        task.StartInfo.ArgumentList.Add("/TN");
        task.StartInfo.ArgumentList.Add("ITSeti-Maintenance-Repair");
        try { task.Start(); }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException) { throw new InvalidOperationException("Не удалось запустить задание восстановления: " + ex.Message, ex); }
        var output = task.StandardOutput.ReadToEndAsync();
        var errors = task.StandardError.ReadToEndAsync();
        await task.WaitForExitAsync();
        if (task.ExitCode != 0) throw new InvalidOperationException($"Задание восстановления недоступно (код {task.ExitCode}): {(await errors).Trim()} {(await output).Trim()}. Переустановите приложение.");
        progress?.Report("Задание Windows запущено. Ожидаем начало DISM/SFC…");
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var root = ReadLatestRoot(InstalledRoot);
            if (root is not null && root != previous) return root;
            await Task.Delay(500);
        }
        throw new TimeoutException("Задание запущено, но папка восстановления не появилась. Проверьте планировщик задач.");
    }

    private static string StartDirect(IProgress<string>? progress)
    {
        var root = Path.Combine(LocalRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var backend = Path.Combine(AppContext.BaseDirectory, "Backend");
        foreach (var name in new[] { "Repair.ps1", "RepairWorker.ps1", "DirectRepair.ps1" }) File.Copy(Path.Combine(backend, name), Path.Combine(root, name));
        File.WriteAllText(Path.Combine(LocalRoot, "latest.txt"), root);
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true, Verb = elevated ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root, Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "DirectRepair.ps1")}\" -JobRoot \"{root}\""
        };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить восстановление");
            progress?.Report("DISM/SFC запущены");
            return root;
        }
        catch (Win32Exception ex) { throw new InvalidOperationException("Восстановлению нужны права администратора: " + ex.Message, ex); }
    }

    private static string? ReadStatus(string root)
    {
        try
        {
            var path = Path.Combine(root, "status.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Не удалось прочитать статус: " + ex.Message; }
    }

    private static string? ReadLatestRoot(string parent)
    {
        try
        {
            var latest = Path.Combine(parent, "latest.txt");
            if (!File.Exists(latest)) return null;
            var root = Path.GetFullPath(File.ReadAllText(latest).Trim());
            return root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root) ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
