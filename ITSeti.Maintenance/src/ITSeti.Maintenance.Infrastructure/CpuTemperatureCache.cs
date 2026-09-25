using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public static class CpuTemperatureCache
{
    public const string TaskName = "ITSeti-Maintenance-Temperature";
    public static string InstalledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "cpu-temperature.json");

    public static CpuTemperatureReading? ReadFresh(string? path = null, TimeSpan? maxAge = null)
    {
        try
        {
            var reading = JsonSerializer.Deserialize<CpuTemperatureReading>(File.ReadAllText(path ?? InstalledPath));
            if (reading is null || reading.CapturedAtUtc is not { } capturedAt
                || capturedAt > DateTimeOffset.UtcNow.AddMinutes(1)
                || DateTimeOffset.UtcNow - capturedAt > (maxAge ?? TimeSpan.FromMinutes(2))) return null;
            return reading;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task<bool> RequestProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/Run", "/TN", TaskName }
            });
            if (process is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
