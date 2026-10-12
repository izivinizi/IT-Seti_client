using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

internal static class HwInfoVoltageStabilizer
{
    private const int WindowSize = 7;
    private const int MinimumSamples = 3;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<double>> Samples = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<HwInfoSensorReading> Stabilize(IEnumerable<HwInfoSensorReading> readings)
    {
        lock (Gate)
        {
            var allReadings = readings.ToArray();
            var stable = allReadings.Where(reading => !IsRail(reading.Category)).ToList();
            var rails = allReadings.Where(reading => IsRail(reading.Category) && double.IsFinite(reading.Value))
                .GroupBy(reading => reading.Category + ":" + reading.Name.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in rails)
            {
                var reading = group.OrderBy(item => Math.Abs(item.Value - Nominal(item.Category))).First();
                if (!IsPlausible(reading)) continue;
                var key = group.Key;
                if (!Samples.TryGetValue(key, out var samples)) Samples[key] = samples = new Queue<double>();
                samples.Enqueue(reading.Value);
                while (samples.Count > WindowSize) samples.Dequeue();
                if (samples.Count < MinimumSamples) continue;

                var ordered = samples.OrderBy(value => value).ToArray();
                stable.Add(reading with { Value = ordered[ordered.Length / 2] });
            }
            return stable;
        }
    }

    internal static void Reset()
    {
        lock (Gate) Samples.Clear();
    }

    private static bool IsRail(string category) =>
        category is "Rail12V" or "Rail5V" or "Rail3V";

    private static double Nominal(string category) =>
        category == "Rail12V" ? 12.0 : category == "Rail5V" ? 5.0 : 3.3;

    private static bool IsPlausible(HwInfoSensorReading reading) => reading.Category switch
    {
        "Rail12V" => reading.Value is >= 9 and <= 15,
        "Rail5V" => reading.Value is >= 3.5 and <= 6.5,
        "Rail3V" => reading.Value is >= 2.4 and <= 4.2,
        _ => false
    };
}
