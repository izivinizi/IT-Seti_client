using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class ApplicationUpdateRunner
{
    private const string UpdateTask = "ITSeti-Maintenance-Update";
    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "Updates", "last-result.txt");

    public static string? ReadLastStatus()
    {
        try
        {
            var status = ReadRawStatus();
            return FilterStatusForVersion(status, Assembly.GetEntryAssembly()?.GetName().Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? ReadRawStatus()
    {
        try { return File.Exists(StatusPath) ? File.ReadAllText(StatusPath).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string GetStatusMessage(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";
        var separator = status.IndexOf('|');
        return (separator >= 0 ? status[(separator + 1)..] : status).Trim();
    }

    public static bool IsReadyToInstall(string? status) =>
        GetStatusMessage(status).StartsWith("Пакет загружен и проверен. Готов к установке.", StringComparison.OrdinalIgnoreCase);

    public static bool IsPendingInstall(string? status)
    {
        var message = GetStatusMessage(status);
        return IsReadyToInstall(status) || message.StartsWith("Пакет загружен и проверен. Ожидает закрытия приложения.", StringComparison.OrdinalIgnoreCase);
    }

    public static string? FilterStatusForVersion(string? status, Version? runningVersion)
    {
        if (string.IsNullOrWhiteSpace(status) || runningVersion is null) return status;
        var match = Regex.Match(status,
            @"(?:Версия\s+(?<version>\d+\.\d+\.\d+(?:\.\d+)?)\s+установлена|Установлена последняя версия\s+(?<version>\d+\.\d+\.\d+(?:\.\d+)?))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var reportedVersion)) return status;
        static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(value.Build, 0));
        return Normalize(reportedVersion) == Normalize(runningVersion) ? status : null;
    }

    public async Task<bool> RequestUpdateAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var requestStarted = DateTime.Now.AddSeconds(-1);
        var requestStartedUtc = DateTime.UtcNow;
        await RunTaskCommandAsync("/Query", cancellationToken);
        await RunTaskCommandAsync("/Run", cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        string? lastMessage = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ReadRawStatus();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var message = GetStatusMessage(status);
                var separator = status.IndexOf('|');
                var hasFreshTimestamp = separator > 0 && DateTime.TryParseExact(
                    status[..separator].Trim(), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var timestamp) && timestamp >= requestStarted
                    && File.GetLastWriteTimeUtc(StatusPath) >= requestStartedUtc;
                if (hasFreshTimestamp && message != lastMessage)
                {
                    lastMessage = message;
                    progress?.Report(message);
                    if (IsReadyToInstall(status)) return true;
                    if (message.StartsWith("Ошибка обновления:", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(message["Ошибка обновления:".Length..].Trim());
                    if (message.Contains("обновление не требуется", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        throw new TimeoutException("Системная задача не сообщила о готовности установщика за 30 минут. Подробности доступны в журнале обновления.");
    }

    private static async Task RunTaskCommandAsync(string operation, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { operation, "/TN", UpdateTask }
            }
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows не запустила планировщик задач.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Задача обновления не установлена. Переустановите приложение.", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            throw new TimeoutException("Планировщик задач не ответил вовремя.");
        }

        if (process.ExitCode != 0)
        {
            var detail = (await stderr).Trim();
            throw new InvalidOperationException(operation == "/Query"
                ? "Задача обновления не установлена. Переустановите приложение."
                : $"Не удалось запустить обновление: {detail}");
        }
        _ = await stdout;
        _ = await stderr;
    }
}
