using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class WindowsUpdateCheckRunner
{
    private sealed record SearchResult(int Count, bool RebootRequired, string[] Titles, int ResultCode);

    public async Task<string> CheckAsync(CancellationToken cancellationToken = default)
    {
        const string script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
            + "$ErrorActionPreference='Stop'; try { $session=New-Object -ComObject Microsoft.Update.Session; "
            + "$session.ClientApplicationID='ITSeti Maintenance'; $searcher=$session.CreateUpdateSearcher(); $searcher.Online=$true; "
            + "$result=$searcher.Search('IsInstalled=0 and IsHidden=0'); $titles=@(); "
            + "for($i=0;$i -lt [Math]::Min(10,$result.Updates.Count);$i++){ $titles+=([string]$result.Updates.Item($i).Title) }; "
            + "@{Count=[int]$result.Updates.Count;RebootRequired=[bool]$result.RebootRequired;Titles=$titles;ResultCode=[int]$result.ResultCode} "
            + "| ConvertTo-Json -Compress } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = new Process { StartInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }
        } };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить проверку Windows Update.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Поиск обновлений не завершился за 5 минут.");
        }
        var errorText = (await error).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? "Windows Update не выполнила поиск." : errorText);
        var result = JsonSerializer.Deserialize<SearchResult>(await output) ?? throw new InvalidDataException("Windows Update вернула пустой результат.");
        if (result.Count == 0) return "Обновлений Windows не найдено. Ничего не устанавливалось.";
        var titles = result.Titles.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Titles.Select(title => "• " + title));
        if (result.Count > result.Titles.Length) titles += Environment.NewLine + $"Ещё обновлений: {result.Count - result.Titles.Length}";
        var reboot = result.RebootRequired ? Environment.NewLine + "После установки может потребоваться перезагрузка." : "";
        return $"Доступно обновлений: {result.Count}. Ничего не устанавливалось.{titles}{reboot}";
    }
}
