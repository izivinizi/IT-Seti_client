using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal sealed record ServiceClientIdentity(bool IsInstalledClient, int ProcessId, int SessionId);

internal static class ServiceClientAuthorization
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    public static ServiceClientIdentity Identify(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId == 0)
            return new(false, -1, -1);
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ITSeti.Maintenance.exe"));
            var actual = process.MainModule?.FileName;
            return new(actual is not null && !process.HasExited && process.SessionId > 0 &&
                string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase),
                (int)processId, process.SessionId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
            or OverflowException or UnauthorizedAccessException)
        {
            return new(false, -1, -1);
        }
    }

    public static bool OrdinaryOperation(MaintenanceServiceOperation operation) => operation is
        MaintenanceServiceOperation.FullCheck or MaintenanceServiceOperation.QuickCheck or
        MaintenanceServiceOperation.Cleanup or
        MaintenanceServiceOperation.UploadReports or MaintenanceServiceOperation.RefreshSoftwareCatalog or
        MaintenanceServiceOperation.ProbeTemperature;
}
