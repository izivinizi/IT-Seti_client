using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record CpuTemperatureReading(double? TemperatureC, string Status, DateTimeOffset? CapturedAtUtc = null);

public static class CpuTemperatureReader
{
    private const int SampleCount = 3;

    public static Task<CpuTemperatureReading> ReadAsync() => Task.Run(Read);

    private static CpuTemperatureReading Read()
    {
        if (!OperatingSystem.IsWindows()) return new(null, "Датчик доступен только в Windows");

        var samples = new List<(string Name, double Value)>(SampleCount);
        var zeroSensors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var computer = new Computer { IsCpuEnabled = true };
        try
        {
            computer.Open();
            for (var sample = 0; sample < SampleCount; sample++)
            {
                var readings = new List<(string Name, double Value)>();
                foreach (var hardware in computer.Hardware.Where(item => item.HardwareType == HardwareType.Cpu))
                    UpdateAndCollect(hardware, readings, zeroSensors);

                if (SelectBestReading(readings) is { } selected)
                    samples.Add(selected);

                if (sample + 1 < SampleCount) Thread.Sleep(150);
            }
        }
        catch (Exception ex)
        {
            return new(null, $"LibreHardwareMonitor: {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
        }
        finally
        {
            try { computer.Close(); } catch { }
        }

        if (samples.Count > 0)
        {
            var ordered = samples.OrderBy(reading => reading.Value).ToArray();
            var selected = ordered[ordered.Length / 2];
            return new(selected.Value, $"LibreHardwareMonitor / PawnIO · {selected.Name} · {samples.Count}/{SampleCount} замера");
        }

        var pawnIoVersion = ReadPawnIoVersion();
        if (pawnIoVersion is null)
            return new(null, "LibreHardwareMonitor: драйвер PawnIO не установлен; установка драйвера могла быть запрещена политикой или антивирусом");

        var zeroDetail = zeroSensors.Count > 0 ? $" Датчики вернули 0 °C: {string.Join(", ", zeroSensors)}." : "";
        return new(null, $"LibreHardwareMonitor: CPU-датчик не доступен при PawnIO {pawnIoVersion} (нет поддержки модели CPU или доступа к устройству).{zeroDetail}");
    }

    private static string? ReadPawnIoVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion")?.ToString();
        }
        catch (System.Security.SecurityException) { return null; }
        catch (IOException) { return null; }
    }

    public static (string Name, double Value)? SelectBestReading(IEnumerable<(string Name, double Value)> readings)
    {
        var candidates = readings
            .Where(reading => !string.IsNullOrWhiteSpace(reading.Name)
                && double.IsFinite(reading.Value) && reading.Value is >= 5 and <= 120
                && !IsDistanceToThermalLimit(reading.Name))
            .ToArray();
        if (candidates.Length == 0) return null;

        return candidates
            .OrderByDescending(reading => GetSensorPriority(reading.Name))
            .ThenByDescending(reading => reading.Value)
            .First();
    }

    private static bool IsDistanceToThermalLimit(string name) =>
        name.Contains("distance", StringComparison.OrdinalIgnoreCase)
        || name.Contains("tjmax", StringComparison.OrdinalIgnoreCase)
        || name.Contains("thermal limit", StringComparison.OrdinalIgnoreCase);

    private static int GetSensorPriority(string name)
    {
        if (name.Contains("tctl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tdie", StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Contains("cpu package", StringComparison.OrdinalIgnoreCase)
            || name.Contains("package id", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Contains("core max", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("ccd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("core average", StringComparison.OrdinalIgnoreCase)
            || name.Contains("average", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains("core", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static void UpdateAndCollect(IHardware hardware, ICollection<(string Name, double Value)> readings, ISet<string> zeroSensors)
    {
        try
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value) continue;
                if (float.IsFinite(value) && value is >= 5 and <= 120) readings.Add((sensor.Name, value));
                else if (value == 0) zeroSensors.Add(sensor.Name);
            }
        }
        catch
        {
            // A missing or access-restricted sensor must not fail the diagnostic.
        }

        foreach (var child in hardware.SubHardware)
            UpdateAndCollect(child, readings, zeroSensors);
    }
}
