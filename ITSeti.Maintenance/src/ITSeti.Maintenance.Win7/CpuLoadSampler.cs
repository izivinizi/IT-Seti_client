using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace ITSeti.Maintenance.Win7
{
    internal static class CpuLoadSampler
    {
        public static double? Read()
        {
            string source;
            return Read(out source);
        }

        public static double? Read(out string source)
        {
            source = "недоступен";
            var systemTimes = TrySystemTimes();
            if (systemTimes.HasValue && systemTimes.Value > 0.5 && systemTimes.Value < 99.5)
            {
                source = "WinAPI GetSystemTimes";
                return systemTimes.Value;
            }
            var raw = TryRawWmi();
            if (raw > 0.5) { source = "WMI PerfRawData"; return raw; }
            var counter = TryPerformanceCounter();
            if (counter > 0.5) { source = "PerformanceCounter"; return counter; }
            var processes = TryProcessTimes();
            if (processes > 0.1) { source = "время процессов"; return processes; }
            if (systemTimes.HasValue) { source = "WinAPI GetSystemTimes (резервные счётчики недоступны)"; return systemTimes.Value; }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Ticks { get { return ((ulong)High << 32) | Low; } }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        private static double? TrySystemTimes()
        {
            FileTime firstIdle, firstKernel, firstUser, secondIdle, secondKernel, secondUser;
            if (!GetSystemTimes(out firstIdle, out firstKernel, out firstUser)) return null;
            Thread.Sleep(1100);
            if (!GetSystemTimes(out secondIdle, out secondKernel, out secondUser)) return null;
            if (secondIdle.Ticks < firstIdle.Ticks || secondKernel.Ticks < firstKernel.Ticks || secondUser.Ticks < firstUser.Ticks)
                return null;
            var elapsed = (secondKernel.Ticks - firstKernel.Ticks) + (secondUser.Ticks - firstUser.Ticks);
            if (elapsed == 0) return null;
            var idle = secondIdle.Ticks - firstIdle.Ticks;
            return Valid(100.0 * (1.0 - (double)idle / elapsed));
        }

        private static double? TryRawWmi()
        {
            try
            {
                var first = RawSample();
                Thread.Sleep(1100);
                var second = RawSample();
                if (first == null || second == null || second.Item2 <= first.Item2) return null;
                var idle = (second.Item1 - first.Item1) / (second.Item2 - first.Item2);
                return Valid((1.0 - idle) * 100.0);
            }
            catch (Exception ex) when (ex is ManagementException || ex is UnauthorizedAccessException || ex is InvalidOperationException) { return null; }
        }

        private static Tuple<double, double> RawSample()
        {
            using (var query = new ManagementObjectSearcher("root\\cimv2",
                "SELECT PercentProcessorTime,TimeStamp_Sys100NS FROM Win32_PerfRawData_PerfOS_Processor WHERE Name='_Total'"))
            using (var values = query.Get())
            {
                var item = values.Cast<ManagementObject>().FirstOrDefault();
                return item == null ? null : Tuple.Create(Convert.ToDouble(item["PercentProcessorTime"]), Convert.ToDouble(item["TimeStamp_Sys100NS"]));
            }
        }

        private static double? TryPerformanceCounter()
        {
            try
            {
                using (var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true))
                {
                    counter.NextValue();
                    Thread.Sleep(1100);
                    return Valid(counter.NextValue());
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { return null; }
        }

        private static double? TryProcessTimes()
        {
            try
            {
                var start = ProcessTimes();
                var watch = Stopwatch.StartNew();
                Thread.Sleep(1100);
                var end = ProcessTimes();
                watch.Stop();
                if (start.Count == 0 || end.Count == 0 || watch.Elapsed.TotalSeconds <= 0) return null;
                var cpuSeconds = end.Where(x => start.ContainsKey(x.Key))
                    .Sum(x => Math.Max(0, (x.Value - start[x.Key]).TotalSeconds));
                return Valid(cpuSeconds / (watch.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100.0);
            }
            catch (Exception) { return null; }
        }

        private static Dictionary<int, TimeSpan> ProcessTimes()
        {
            var result = new Dictionary<int, TimeSpan>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == 0 || process.ProcessName.Equals("Idle", StringComparison.OrdinalIgnoreCase)) continue;
                        result[process.Id] = process.TotalProcessorTime;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { }
                }
            }
            return result;
        }

        private static double? Valid(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100.5) return null;
            return Math.Max(0, Math.Min(100, value));
        }
    }
}
