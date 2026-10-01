using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class WindowsUpdatePolicyRunner
{
    private static readonly string ResultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "WindowsUpdate", "policy-status.json");
    private const string DisableTask = "ITSeti-Maintenance-DisableUpdates";
    private const string RestoreTask = "ITSeti-Maintenance-RestoreUpdates";

    private sealed record PolicyResult(string Action, string State, string Message, DateTimeOffset UpdatedAt);

    public string ReadStatus()
    {
        try
        {
            if (!File.Exists(ResultPath)) return "Автоматические обновления не изменялись приложением";
            return JsonSerializer.Deserialize<PolicyResult>(File.ReadAllText(ResultPath))?.Message
                ?? "Статус автообновлений недоступен";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return "Статус автообновлений недоступен: " + ex.Message; }
    }

    public Task<string> DisableAsync() => RunTaskAsync(DisableTask, "Disable");
    public Task<string> RestoreAsync() => RunTaskAsync(RestoreTask, "Restore");

    private static async Task<string> RunTaskAsync(string taskName, string action)
    {
        var previous = ReadResult()?.UpdatedAt ?? DateTimeOffset.MinValue;
        Process? process;
        try { process = Process.Start(new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/Run", "/TN", taskName }
        }); }
        catch (Exception ex) when (IsAdministrator() && ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return await RunScriptDirectAsync(action); }
        if (process is null)
        {
            if (IsAdministrator()) return await RunScriptDirectAsync(action);
            throw new InvalidOperationException("Не удалось запустить системную задачу Windows Update.");
        }
        using (process)
        {
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            _ = await error;
            _ = await output;
            if (IsAdministrator()) return await RunScriptDirectAsync(action);
            throw new InvalidOperationException($"Задача Windows Update недоступна (код {process.ExitCode}).");
        }
        _ = await error;
        _ = await output;
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (ReadResult() is { } result && result.UpdatedAt > previous)
                {
                    if (result?.Action == action) return result.Message;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Системная задача запущена, но результат настройки Windows Update не появился.");
    }

    private static async Task<string> RunScriptDirectAsync(string action)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Backend", "Set-WindowsAutomaticUpdates.ps1");
        if (!File.Exists(script)) throw new InvalidOperationException("Системный скрипт настройки обновлений не найден рядом с приложением.");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action }
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить настройку Windows Update с повышенными правами.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Настройка Windows Update не завершилась за 30 секунд.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Скрипт Windows Update завершился с кодом {process.ExitCode}.");
        return new WindowsUpdatePolicyRunner().ReadStatus();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static PolicyResult? ReadResult()
    {
        try { return File.Exists(ResultPath) ? JsonSerializer.Deserialize<PolicyResult>(File.ReadAllText(ResultPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
