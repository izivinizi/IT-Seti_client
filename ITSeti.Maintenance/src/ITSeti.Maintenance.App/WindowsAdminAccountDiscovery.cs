using System.Diagnostics;
using System.IO;
using System.Text;

namespace ITSeti.Maintenance.App;

internal static class WindowsAdminAccountDiscovery
{
    public static IReadOnlyList<string> FindCandidates()
    {
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return [];

            const string command = "$ErrorActionPreference='Stop'; "
                + "$g=Get-WmiObject Win32_Group -Filter \"LocalAccount=True AND SID='S-1-5-32-544'\"; "
                + "if($g){$g.GetRelated('Win32_UserAccount') | Where-Object {!$_.Disabled -and !$_.Lockout -and $_.Domain -and $_.Name} | "
                + "ForEach-Object {$n=[string]$_.Domain+[char]92+[string]$_.Name; "
                + "[Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($n)))}}";
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

            using var process = Process.Start(start);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }
            if (process.ExitCode != 0) return [];

            var candidates = output.GetAwaiter().GetResult().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim())))
                .Where(name => name.Contains('\\') && !name.EndsWith("\\", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.OrderBy(AccountPriority).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static int AccountPriority(string account)
    {
        var name = account[(account.LastIndexOf('\\') + 1)..];
        if (name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("it-seti", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("user", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}
