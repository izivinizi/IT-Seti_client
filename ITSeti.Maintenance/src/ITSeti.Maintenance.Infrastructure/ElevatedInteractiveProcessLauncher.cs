using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ITSeti.Maintenance.Infrastructure;

// The service owns the privileged token. GUI processes are moved into the
// caller's interactive session so they do not disappear into Session 0.
public static class ElevatedInteractiveProcessLauncher
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;

    public static int LaunchAsSystem(int sessionId, string executable, IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Разрешённая утилита не найдена.", executable);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(workingDirectory);

        var desiredAccess = TokenDuplicate | TokenQuery | TokenAssignPrimary |
            TokenAdjustDefault | TokenAdjustSessionId;
        if (!OpenProcessToken(GetCurrentProcess(), desiredAccess, out var sourceToken))
            ThrowLastError("Не удалось получить токен службы.");

        try
        {
            if (!DuplicateTokenEx(sourceToken, desiredAccess, IntPtr.Zero, SecurityImpersonation,
                    TokenPrimary, out var primaryToken))
                ThrowLastError("Не удалось подготовить повышенный токен.");
            try
            {
                var session = sessionId;
                if (!SetTokenInformation(primaryToken, TokenSessionId, ref session,
                        (uint)Marshal.SizeOf<int>()))
                    ThrowLastError("Не удалось выбрать интерактивный сеанс Windows.");

                if (!CreateEnvironmentBlock(out var environment, primaryToken, false))
                    ThrowLastError("Не удалось подготовить окружение процесса.");
                try
                {
                    var startup = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfo>(),
                        Desktop = @"winsta0\default"
                    };
                    var commandLine = new System.Text.StringBuilder(
                        Quote(executable) + string.Concat(arguments.Select(argument => " " + Quote(argument))));
                    if (!CreateProcessAsUser(primaryToken, executable, commandLine, IntPtr.Zero, IntPtr.Zero,
                            false, CreateUnicodeEnvironment | CreateNewConsole, environment, workingDirectory,
                            ref startup, out var process))
                        ThrowLastError("Windows не запустила повышенную утилиту.");
                    try { return checked((int)process.ProcessId); }
                    finally
                    {
                        CloseHandle(process.ThreadHandle);
                        CloseHandle(process.ProcessHandle);
                    }
                }
                finally { DestroyEnvironmentBlock(environment); }
            }
            finally { CloseHandle(primaryToken); }
        }
        finally { CloseHandle(sourceToken); }
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void ThrowLastError(string message) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr primaryToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        ref int tokenInformation, uint tokenInformationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName,
        System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }
}
