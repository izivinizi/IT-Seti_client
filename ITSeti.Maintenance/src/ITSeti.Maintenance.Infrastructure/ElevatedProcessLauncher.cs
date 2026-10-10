using System.Diagnostics;
using System.Security.Principal;

namespace ITSeti.Maintenance.Infrastructure;

internal static class ElevatedProcessLauncher
{
    public static bool IsCurrentProcessElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory, string? argument = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            // Portable disk utilities stay in the user's desktop session.
            // Privileged checks use the installed SYSTEM task instead.
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = false,
            Arguments = ""
        };

        if (argument is not null) start.ArgumentList.Add(argument);

        return start;
    }
}
