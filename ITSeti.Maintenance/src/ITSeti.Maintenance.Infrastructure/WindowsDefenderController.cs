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
    private static readonly string StatusPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "WindowsDefender", "status.json");

    public Task<WindowsDefenderState> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Status", cancellationToken);

    public Task<WindowsDefenderState> SetRealTimeProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return RunAsync(enabled ? "Enable" : "Disable", cancellationToken);
    }

    private static async Task<WindowsDefenderState> RunAsync(string action, CancellationToken cancellationToken)
    {
        if (action != "Status")
        {
            var operation = action == "Enable" ? MaintenanceServiceOperation.EnableWindowsDefender : MaintenanceServiceOperation.DisableWindowsDefender;
            var previousWrite = File.Exists(StatusPath) ? File.GetLastWriteTimeUtc(StatusPath) : DateTime.MinValue;
            var service = await MaintenanceServiceClient.StartAsync(operation, cancellationToken);
            if (service.Connected)
            {
                if (!service.Accepted) throw new InvalidOperationException(service.Message);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(StatusPath) && File.GetLastWriteTimeUtc(StatusPath) > previousWrite)
                    {
                        var completed = await ReadResultAsync(StatusPath, cancellationToken);
                        if (completed is { } completedResult)
                        {
                            if (!completedResult.Success) throw new InvalidOperationException("Windows не изменила защиту: " + (completedResult.Error ?? "операция завершилась ошибкой."));
                            var expected = action == "Enable";
                            if (completedResult.Available && completedResult.RealTimeProtectionEnabled == expected)
                                return ToState(completedResult);
                            throw new InvalidOperationException("Служба завершила команду, но состояние защиты не изменилось. Проверьте Tamper Protection и политику организации.");
                        }
                    }
                    await Task.Delay(250, cancellationToken);
                }
                throw new TimeoutException("Служба не вернула результат изменения Microsoft Defender за 30 секунд.");
            }
            if (!IsAdministrator()) throw new InvalidOperationException("Служба обслуживания недоступна; запустить изменение без повышения прав нельзя.");
        }
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

        return ToState(result);
    }

    private static WindowsDefenderState ToState(CommandResult result)
    {
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

    private static async Task<CommandResult?> ReadResultAsync(string path, CancellationToken cancellationToken)
    {
        try { return JsonSerializer.Deserialize<CommandResult>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
