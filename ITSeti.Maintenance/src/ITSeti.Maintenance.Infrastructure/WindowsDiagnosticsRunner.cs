using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class WindowsDiagnosticsRunner : IDiagnosticsRunner
{
    public Task<DiagnosticSnapshot> RunAsync(CancellationToken cancellationToken = default) => CaptureAsync(true, true, cancellationToken);

    public Task<DiagnosticSnapshot> RunLiveAsync(CancellationToken cancellationToken = default) => CaptureAsync(false, false, cancellationToken);

    private static Task<DiagnosticSnapshot> CaptureAsync(bool includeDiskHealth, bool readTemperature, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var started = DateTimeOffset.Now;
        (ulong Idle, ulong Kernel, ulong User)? before = null;
        try { before = ReadCpu(); } catch (Win32Exception) { }
        await Task.Delay(1500, cancellationToken);
        (ulong Idle, ulong Kernel, ulong User)? after = null;
        try { after = ReadCpu(); } catch (Win32Exception) { }
        double? nativeCpu = null;
        if (before is { } first && after is { } second &&
            second.Idle >= first.Idle && second.Kernel >= first.Kernel && second.User >= first.User)
        {
            var total = (second.Kernel - first.Kernel) + (second.User - first.User);
            if (total > 0) nativeCpu = Math.Clamp(100.0 * (total - Math.Min(total, second.Idle - first.Idle)) / total, 0, 100);
        }
        var cpu = nativeCpu is > 0.5 and < 99.5
            ? nativeCpu.Value : await ReadCpuFallbackAsync(cancellationToken) ?? nativeCpu ?? -1;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var disks = new List<DiskSnapshot>();
        var notes = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady) { notes.Add($"{drive.Name}: раздел недоступен."); continue; }
                disks.Add(new DiskSnapshot(drive.Name, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notes.Add($"{drive.Name}: {ex.Message}"); }
        }
        var physicalDisks = includeDiskHealth ? await ReadDiskHealthAsync(cancellationToken) : [];
        if (includeDiskHealth && physicalDisks.Count == 0) notes.Add("Состояние накопителей через Windows определить не удалось. Для подробной проверки запустите полную диагностику.");
        var windows = WindowsVersionInfo.Read();
        var temperature = readTemperature
            ? await CpuTemperatureCache.RequestFreshAsync(TimeSpan.FromSeconds(5), cancellationToken)
            : CpuTemperatureCache.ReadFresh(maxAge: TimeSpan.FromSeconds(45));
        temperature ??= new CpuTemperatureReading(null, "Ожидается опрос системной задачи");
        return new DiagnosticSnapshot(Guid.NewGuid(), started, Environment.MachineName, cpu,
            memory.TotalPhysical, memory.AvailablePhysical, disks, notes, QuickDisks: physicalDisks,
            LastBootAt: DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            WindowsEdition: windows.Edition, WindowsRelease: windows.Release, WindowsBuild: windows.Build,
            CpuTemperatureC: temperature.TemperatureC, CpuTemperatureStatus: temperature.Status,
            ActiveUptimeHours: ReadActiveUptimeHours());
    }, cancellationToken);

    private static async Task<List<PhysicalDiskDetails>> ReadDiskHealthAsync(CancellationToken cancellationToken)
    {
        const string script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
            + "$rows=@(if(Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue){"
            + "Get-PhysicalDisk -ErrorAction Stop | ForEach-Object { [pscustomobject]@{Model=[string]$_.FriendlyName;MediaType=[string]$_.MediaType;Health=[string]$_.HealthStatus} }"
            + "}else{Get-CimInstance Win32_DiskDrive -ErrorAction Stop | ForEach-Object { [pscustomobject]@{Model=[string]$_.Model;MediaType='Неизвестно';Health=[string]$_.Status} }}); "
            + "ConvertTo-Json -InputObject $rows -Compress";
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            using var process = new Process { StartInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }
            } };
            if (!process.Start()) return [];
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return [];
            }
            if (process.ExitCode != 0) return [];
            _ = await error;
            return JsonSerializer.Deserialize<List<PhysicalDiskDetails>>(await output) ?? [];
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException) { return []; }
    }

    private static (ulong Idle, ulong Kernel, ulong User) ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (idle.Value, kernel.Value, user.Value);
    }

    private static double? ReadActiveUptimeHours()
    {
        return QueryUnbiasedInterruptTime(out var ticks) ? ticks / 36_000_000_000.0 : null;
    }

    private static async Task<double?> ReadCpuFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            using var process = new Process { StartInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                ArgumentList = { "-NoProfile", "-Command",
                    "([double](Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter \"Name='_Total'\" -ErrorAction Stop).PercentProcessorTime).ToString([Globalization.CultureInfo]::InvariantCulture)" }
            } };
            if (!process.Start()) return await ReadProcessCpuAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return await ReadProcessCpuAsync(cancellationToken);
            }
            double? wmi = process.ExitCode == 0 && double.TryParse((await output).Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100
                ? value : null;
            return wmi is > 0.5 and < 99.5 ? wmi : await ReadProcessCpuAsync(cancellationToken) ?? wmi;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return await ReadProcessCpuAsync(cancellationToken);
        }
    }

    private static async Task<double?> ReadProcessCpuAsync(CancellationToken cancellationToken)
    {
        static Dictionary<int, TimeSpan> Sample()
        {
            var times = new Dictionary<int, TimeSpan>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (process.Id != 0) times[process.Id] = process.TotalProcessorTime; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
                }
            }
            return times;
        }
        try
        {
            var before = Sample();
            var watch = Stopwatch.StartNew();
            await Task.Delay(750, cancellationToken);
            var after = Sample();
            watch.Stop();
            if (before.Count == 0 || after.Count == 0) return null;
            var seconds = after.Where(item => before.ContainsKey(item.Key))
                .Sum(item => Math.Max(0, (item.Value - before[item.Key]).TotalSeconds));
            return Math.Clamp(seconds / (watch.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100, 0, 100);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
