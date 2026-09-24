using System.Diagnostics;
using System.Text;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class WindowsEventMessageResolver
{
    public async Task<string> GetMessageAsync(EventDetails item)
    {
        if (item.RecordId <= 0 || item.Log is not ("System" or "Application")) return item.Message;
        var log = item.Log;
        var command = "$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
            + $"$e=Get-WinEvent -LogName '{log}' -FilterXPath '*[System[(EventRecordID={item.RecordId})]]' -ErrorAction Stop; "
            + "if($e.Message){$e.Message}else{'Источник не вернул описание события.'}";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))
            }
        };
        try
        {
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = (await output).Trim();
            if (!string.IsNullOrEmpty(text)) return text;
            var reason = (await error).Trim();
            return string.IsNullOrEmpty(reason) ? "Текст события отсутствует." : "Не удалось прочитать описание: " + reason;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            return "Описание события не получено за 12 секунд.";
        }
        catch (Exception ex) { return "Не удалось прочитать описание: " + ex.Message; }
    }
}
