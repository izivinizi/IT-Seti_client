using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Service;

public sealed class MonitoringSensorHistory
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaximumSamples = 61;
    private const int MaximumSensorsPerSample = 24;
    private readonly List<SensorHistorySample> samples = [];

    public IReadOnlyList<SensorHistorySample> Add(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var capturedAt = snapshot.StartedAt.ToUniversalTime();
        var sensorsAreFresh = snapshot.HwInfo is { } hwInfo &&
            hwInfo.CapturedAtUtc <= capturedAt.AddSeconds(2) &&
            capturedAt - hwInfo.CapturedAtUtc <= TimeSpan.FromSeconds(30);
        var sensors = sensorsAreFresh
            ? snapshot.HwInfo!.Sensors
                .Where(sensor => IsChartable(sensor.Category) && double.IsFinite(sensor.Value))
                .OrderByDescending(sensor => sensor.IsCritical)
                .ThenByDescending(sensor => sensor.IsWarning)
                .ThenBy(sensor => sensor.Category, StringComparer.Ordinal)
                .Take(MaximumSensorsPerSample)
                .Select(sensor => new SensorHistoryReading(sensor.Name, sensor.Value, sensor.Unit, sensor.Category))
                .ToArray()
            : [];
        var sample = new SensorHistorySample(capturedAt,
            IsPercent(snapshot.CpuPercent) ? snapshot.CpuPercent : null,
            IsPercent(snapshot.MemoryUsedPercent) ? snapshot.MemoryUsedPercent : null,
            sensors, sensorsAreFresh ? snapshot.HwInfo!.CapturedAtUtc : null);

        if (samples.Count > 0 && capturedAt <= samples[^1].CapturedAtUtc)
        {
            if (capturedAt == samples[^1].CapturedAtUtc) samples[^1] = sample;
            else samples.Clear();
        }
        else samples.Add(sample);

        var cutoff = capturedAt - Window;
        samples.RemoveAll(item => item.CapturedAtUtc < cutoff);
        while (samples.Count > MaximumSamples) samples.RemoveAt(0);
        return Snapshot();
    }

    public IReadOnlyList<SensorHistorySample> Snapshot() => samples.ToArray();

    private static bool IsPercent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;

    private static bool IsChartable(string category) => category is
        "CpuTemperature" or "VrmTemperature" or "PchTemperature" or "Rail12V" or "Rail5V" or
        "Rail3V" or "CpuVoltage" or "CpuLoad" or "MemoryLoad";
}
