using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ITSeti.Maintenance.Infrastructure;

internal static class InteractiveUserProcessLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint RequiredTokenAccess = 0x0001 | 0x0002 | 0x0008;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint LogonWithProfile = 0x00000001;

    public static Process StartPowerShell(string arguments, string workingDirectory, string userSid)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (var shell in Process.GetProcessesByName("explorer"))
        {
            using (shell)
            {
                if (shell.SessionId != sessionId) continue;
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, shell.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (!OpenProcessToken(handle, RequiredTokenAccess, out var token)) continue;
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    {
                        if (!string.Equals(identity.User?.Value, userSid, StringComparison.OrdinalIgnoreCase)) continue;
                        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                        var command = new StringBuilder($"\"{executable}\" {arguments}");
                        if (!CreateEnvironmentBlock(out var environment, token, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить окружение пользователя для очистки.");
                        try
                        {
                            if (!CreateProcessWithTokenW(token, LogonWithProfile, executable, command,
                                    CreateNoWindow | CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out var created))
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось запустить очистку под учётной записью пользователя.");
                            try { return Process.GetProcessById(created.ProcessId); }
                            finally
                            {
                                CloseHandle(created.ThreadHandle);
                                CloseHandle(created.ProcessHandle);
                            }
                        }
                        finally { DestroyEnvironmentBlock(environment); }
                    }
                }
                finally { CloseHandle(handle); }
            }
        }
        throw new InvalidOperationException("Не найден активный сеанс исходного пользователя. Очистка его профиля не запускалась.");
    }

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
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags,
        string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
