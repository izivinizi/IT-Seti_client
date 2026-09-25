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

                var preferred = readings.Where(IsPackageOrCoreTemperature).ToArray();
                var cpuReadings = preferred.Length > 0 ? preferred : readings.ToArray();
                if (cpuReadings.Length > 0)
                    samples.Add(cpuReadings.OrderByDescending(reading => reading.Value).First());

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

    private static bool IsPackageOrCoreTemperature((string Name, double Value) reading) =>
        reading.Name.Contains("package", StringComparison.OrdinalIgnoreCase)
        || reading.Name.Contains("tctl", StringComparison.OrdinalIgnoreCase)
        || reading.Name.Contains("tdie", StringComparison.OrdinalIgnoreCase)
        || reading.Name.Contains("ccd", StringComparison.OrdinalIgnoreCase)
        || reading.Name.Contains("core", StringComparison.OrdinalIgnoreCase)
        || reading.Name.Contains("average", StringComparison.OrdinalIgnoreCase);

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
