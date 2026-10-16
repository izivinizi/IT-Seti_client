using System.Runtime.InteropServices;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class DiskLatencySampler : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private IntPtr query;
    private IntPtr readCounter;
    private IntPtr writeCounter;
    private DateTimeOffset retryAfterUtc;
    private bool disposed;
    public IReadOnlyList<DiskLatencyReading> LastReadings { get; private set; } = [];

    public (double? ReadMs, double? WriteMs) Read()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        lock (gate)
        {
            LastReadings = [];
            if (disposed || !EnsureQuery()) return (null, null);
            if (PdhCollectQueryData(query) != 0) { ResetQuery(); RetryLater(); return (null, null); }
            var reads = ReadArray(readCounter);
            var writes = ReadArray(writeCounter);
            LastReadings = reads.Keys.Concat(writes.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                .Select(name => new DiskLatencyReading(name, reads.GetValueOrDefault(name), writes.GetValueOrDefault(name)))
                .ToArray();
            return (LastReadings.MaxBy(value => value.ReadMs)?.ReadMs, LastReadings.MaxBy(value => value.WriteMs)?.WriteMs);
        }
    }

    private bool EnsureQuery()
    {
        if (query != IntPtr.Zero) return true;
        if (DateTimeOffset.UtcNow < retryAfterUtc) return false;

        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
        {
            RetryLater();
            return false;
        }
        if (PdhAddEnglishCounter(query, @"\PhysicalDisk(*)\Avg. Disk sec/Read", UIntPtr.Zero, out readCounter) != 0 ||
            PdhAddEnglishCounter(query, @"\PhysicalDisk(*)\Avg. Disk sec/Write", UIntPtr.Zero, out writeCounter) != 0 ||
            PdhCollectQueryData(query) != 0)
        {
            ResetQuery();
            RetryLater();
            return false;
        }
        return true;
    }

    private static Dictionary<string, double?> ReadArray(IntPtr counter)
    {
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        uint bytes = 0, count = 0;
        const uint moreData = 0x800007D2;
        if (PdhGetFormattedCounterArray(counter, PdhFormatDouble, ref bytes, out count, IntPtr.Zero) != moreData ||
            bytes == 0 || bytes > 1024 * 1024) return values;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFormatDouble, ref bytes, out count, buffer) != 0) return values;
            var itemSize = Marshal.SizeOf<CounterItem>();
            if (count > bytes / itemSize) return values;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * itemSize));
                var name = Marshal.PtrToStringUni(item.Name);
                var seconds = item.Value.Seconds;
                if (name is not null && item.Value.Status is 0 or 1 && double.IsFinite(seconds) && seconds is >= 0 and <= 60)
                    values[name] = seconds * 1000;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return values;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Seconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    private void RetryLater() => retryAfterUtc = DateTimeOffset.UtcNow + RetryDelay;

    private void ResetQuery()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = readCounter = writeCounter = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ResetQuery();
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
