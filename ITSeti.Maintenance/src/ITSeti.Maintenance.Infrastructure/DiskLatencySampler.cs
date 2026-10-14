using System.Runtime.InteropServices;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class DiskLatencySampler : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private IntPtr query;
    private IntPtr readCounter;
    private IntPtr writeCounter;
    private IntPtr valueBuffer;
    private DateTimeOffset retryAfterUtc;
    private bool disposed;

    public (double? ReadMs, double? WriteMs) Read()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        lock (gate)
        {
            if (disposed || !EnsureQuery()) return (null, null);
            if (PdhCollectQueryData(query) != 0) { ResetQuery(); return (null, null); }
            return (ReadMilliseconds(readCounter), ReadMilliseconds(writeCounter));
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
        if (PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Avg. Disk sec/Read", UIntPtr.Zero, out readCounter) != 0 ||
            PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Avg. Disk sec/Write", UIntPtr.Zero, out writeCounter) != 0 ||
            PdhCollectQueryData(query) != 0)
        {
            ResetQuery();
            RetryLater();
            return false;
        }
        valueBuffer = Marshal.AllocHGlobal(16);
        return true;
    }

    private double? ReadMilliseconds(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, PdhFormatDouble, out _, valueBuffer) != 0)
            return null;
        var status = unchecked((uint)Marshal.ReadInt32(valueBuffer));
        if (status is not (0 or 1)) return null;
        var offset = IntPtr.Size == 8 ? 8 : 4;
        var seconds = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(valueBuffer, offset));
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 60) return null;
        return seconds * 1000;
    }

    private void RetryLater() => retryAfterUtc = DateTimeOffset.UtcNow + RetryDelay;

    private void ResetQuery()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = readCounter = writeCounter = IntPtr.Zero;
        if (valueBuffer != IntPtr.Zero) Marshal.FreeHGlobal(valueBuffer);
        valueBuffer = IntPtr.Zero;
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

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType, IntPtr value);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
