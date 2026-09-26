using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ITSeti.Maintenance.Infrastructure;

public static class LocalAdminAccountRunner
{
    public static async Task<string> CreateAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Укажите пароль новой учётной записи.", nameof(password));
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Для создания учётной записи откройте инженерное окно с правами администратора Windows.");

        var script = Path.Combine(AppContext.BaseDirectory, "Backend", "Create-LocalAdmin.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Не найден проверенный скрипт создания учётной записи.", script);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось запустить создание учётной записи.", ex);
        }
        await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Создание учётной записи не завершилось за две минуты.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
        return (await output).Trim();
    }
}
