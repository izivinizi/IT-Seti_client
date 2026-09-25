using System.Diagnostics;
using System.Security.Principal;

namespace ITSeti.Maintenance.Infrastructure;

internal static class ElevatedProcessLauncher
{
    public static bool IsCurrentProcessElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory,
        string? argument = null, bool? currentProcessElevated = null)
    {
        var elevated = currentProcessElevated ?? IsCurrentProcessElevated;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = !elevated,
            WorkingDirectory = workingDirectory,
            Arguments = elevated ? "" : argument ?? ""
        };

        if (elevated)
        {
            if (argument is not null) start.ArgumentList.Add(argument);
        }
        else
        {
            start.Verb = "runas";
        }

        return start;
    }
}
