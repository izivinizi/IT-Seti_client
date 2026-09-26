using System.ComponentModel;
using System.Diagnostics;
using System.Security;
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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path ?? InstalledPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var reading = JsonSerializer.Deserialize<CpuTemperatureReading>(stream);
                if (reading is null || reading.CapturedAtUtc is not { } capturedAt
                    || capturedAt > DateTimeOffset.UtcNow.AddMinutes(1)
                    || DateTimeOffset.UtcNow - capturedAt > (maxAge ?? TimeSpan.FromMinutes(2))
                    || string.IsNullOrWhiteSpace(reading.Status)
                    || reading.TemperatureC is { } value && (!double.IsFinite(value) || value is < 5 or > 120)) return null;
                return reading;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or SecurityException or ArgumentException or NotSupportedException)
            {
                if (attempt == 4) return null;
                Thread.Sleep(20 * (attempt + 1));
            }
        }
        return null;
    }

    public static async Task<CpuTemperatureReading?> RequestFreshAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var cached = ReadFresh(maxAge: TimeSpan.FromSeconds(45));
        if (cached?.TemperatureC is not null
            || cached?.CapturedAtUtc is { } recent && DateTimeOffset.UtcNow - recent < TimeSpan.FromSeconds(10))
            return cached;

        var requestedAt = DateTimeOffset.UtcNow;
        if (!await RequestProbeAsync(cancellationToken)) return cached;

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = ReadFresh(maxAge: TimeSpan.FromMinutes(5));
            if (updated?.CapturedAtUtc is { } capturedAt && capturedAt >= requestedAt.AddSeconds(-1))
                return updated;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return cached;
    }

    public static async Task<bool> RequestProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
            using var process = Process.Start(new ProcessStartInfo(scheduler)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/Run", "/TN", TaskName }
            });
            if (process is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
