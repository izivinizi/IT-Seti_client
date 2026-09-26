using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record WindowsDefenderState(
    bool Available,
    bool? RealTimeProtectionEnabled,
    bool TamperProtectionEnabled,
    string Mode,
    string Message);

public sealed class WindowsDefenderController
{
    private sealed record CommandResult(
        bool Success,
        bool Available,
        bool? RealTimeProtectionEnabled,
        bool? TamperProtectionEnabled,
        string? Mode,
        string? Error);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "Backend", "WindowsDefender.ps1");

    public Task<WindowsDefenderState> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Status", cancellationToken);

    public Task<WindowsDefenderState> SetRealTimeProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
            throw new UnauthorizedAccessException("Изменять параметры Microsoft Defender можно только из инженерского окна с правами администратора.");

        return RunAsync(enabled ? "Enable" : "Disable", cancellationToken);
    }

    private static async Task<WindowsDefenderState> RunAsync(string action, CancellationToken cancellationToken)
    {
        if (!File.Exists(ScriptPath)) throw new FileNotFoundException("Скрипт управления Microsoft Defender не найден.", ScriptPath);

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath, "-Action", action }
            }
        };

        if (!process.Start()) throw new InvalidOperationException("Windows не запустила проверку Microsoft Defender.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            throw new TimeoutException("Microsoft Defender не ответил за 20 секунд.");
        }

        var output = await stdout;
        var errorOutput = (await stderr).Trim();
        CommandResult? result;
        try { result = JsonSerializer.Deserialize<CommandResult>(output.Trim(), JsonOptions); }
        catch (JsonException ex)
        {
            var detail = string.IsNullOrWhiteSpace(errorOutput) ? ex.Message : errorOutput;
            throw new InvalidOperationException("Не удалось прочитать ответ Microsoft Defender: " + detail, ex);
        }

        if (result is null) throw new InvalidOperationException("Microsoft Defender не вернул состояние защиты.");
        if (!result.Success)
        {
            if (action == "Status")
                return new(false, null, result.TamperProtectionEnabled ?? false, result.Mode ?? "",
                    "Не удалось получить состояние Microsoft Defender: " + (result.Error ?? errorOutput));
            var message = string.Equals(result.Error, "DEFENDER_NOT_ACTIVE", StringComparison.Ordinal)
                ? "Microsoft Defender не является активным антивирусом на этом компьютере."
                : "Windows отклонила изменение. Возможны Tamper Protection или политика организации. " + (result.Error ?? errorOutput);
            throw new InvalidOperationException(message);
        }

        var enabled = result.RealTimeProtectionEnabled;
        var stateMessage = !result.Available
            ? "Microsoft Defender не активен; изменение состояния недоступно."
            : enabled switch
            {
                true => "Защита Microsoft Defender в реальном времени включена.",
                false => "Защита Microsoft Defender в реальном времени отключена.",
                _ => "Состояние защиты Microsoft Defender недоступно."
            };
        if (!string.IsNullOrWhiteSpace(result.Mode)) stateMessage += " Режим: " + result.Mode + ".";

        return new(result.Available, enabled, result.TamperProtectionEnabled ?? false, result.Mode ?? "", stateMessage);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
