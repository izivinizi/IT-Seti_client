using System.ComponentModel;
using System.Diagnostics;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class ApplicationUpdateRunner
{
    private const string UpdateTask = "ITSeti-Maintenance-Update";

    public async Task RequestUpdateAsync(CancellationToken cancellationToken = default)
    {
        await RunTaskCommandAsync("/Query", cancellationToken);
        await RunTaskCommandAsync("/Run", cancellationToken);
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
