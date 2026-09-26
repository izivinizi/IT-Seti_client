using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ITSeti.Maintenance.App;

internal static class EngineerWindowLauncher
{
    private const string BootstrapArgument = "--engineer-bootstrap";
    private const string EngineerArgument = "--engineer-window";
    private const string DatabaseArgument = "--history-db";

    public sealed record CredentialLaunchResult(Process? Process, int ErrorCode);

    public static CredentialLaunchResult TryStartWithWindowsCredentials(string account, SecureString password, string historyDatabase)
    {
        var (userName, domain) = ParseAccount(account);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            UserName = userName,
            Domain = domain,
            Password = password,
            LoadUserProfile = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(BootstrapArgument);
        start.ArgumentList.Add(DatabaseArgument);
        start.ArgumentList.Add(Path.GetFullPath(historyDatabase));

        var passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var authenticated = LogonUserW(userName, string.IsNullOrEmpty(domain) ? null : domain, passwordPointer,
                LogonInteractive, LogonProviderDefault, out var token);
            using (token)
            {
                if (!authenticated) return new(null, Marshal.GetLastWin32Error());
                return new(Process.Start(start)
                    ?? throw new InvalidOperationException("Windows не запустила инженерский процесс."), 0);
            }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer); }
    }

    public static bool HandleBootstrap(string[] args)
    {
        if (!args.Contains(BootstrapArgument, StringComparer.OrdinalIgnoreCase)) return false;

        var database = GetArgument(args, DatabaseArgument)
            ?? throw new InvalidOperationException("Не передан путь к базе истории.");
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        var elevated = IsAdministrator();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (!elevated) start.Verb = "runas";
        start.ArgumentList.Add(EngineerArgument);
        start.ArgumentList.Add(DatabaseArgument);
        start.ArgumentList.Add(Path.GetFullPath(database));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows не запустила инженерское окно.");
        return true;
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static (string UserName, string Domain) ParseAccount(string value)
    {
        var account = value.Trim();
        if (account.Length == 0) throw new ArgumentException("Укажите учётную запись Windows.", nameof(value));

        var slash = account.LastIndexOf('\\');
        if (slash >= 0)
        {
            if (slash == 0 || slash == account.Length - 1)
                throw new ArgumentException("Введите учётную запись в формате ПК\\пользователь или ДОМЕН\\пользователь.", nameof(value));
            return (account[(slash + 1)..], account[..slash]);
        }

        if (account.Contains('@')) return (account, string.Empty);
        return (account, Environment.MachineName);
    }

    private const int LogonInteractive = 2;
    private const int LogonProviderDefault = 0;

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(string userName, string? domain, IntPtr password,
        int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
