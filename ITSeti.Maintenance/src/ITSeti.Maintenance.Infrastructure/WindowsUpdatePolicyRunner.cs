using System.Diagnostics;
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
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/Run", "/TN", taskName }
        }) ?? throw new InvalidOperationException("Не удалось запустить системную задачу Windows Update.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Задача недоступна (код {process.ExitCode}): {(await error).Trim()} {(await output).Trim()}.");

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

    private static PolicyResult? ReadResult()
    {
        try { return File.Exists(ResultPath) ? JsonSerializer.Deserialize<PolicyResult>(File.ReadAllText(ResultPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
