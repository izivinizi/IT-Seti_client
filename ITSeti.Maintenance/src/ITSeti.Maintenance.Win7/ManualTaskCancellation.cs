using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ITSeti.Maintenance.Win7
{
    internal static class ManualTaskCancellation
    {
        private static string PathFor(string name) => Path.Combine(ScheduledCheckRunner.ResultsRoot, name + "-cancel.txt");
        private static string StoppedPathFor(string name) => Path.Combine(ScheduledCheckRunner.ResultsRoot, name + "-stopped.txt");

        internal static void Begin(string name)
        {
            if (!File.Exists(PathFor(name)) || !File.Exists(StoppedPathFor(name)))
                throw new InvalidOperationException("Системные задачи диагностики не настроены. Переустановите приложение.");
            using (var process = Process.GetCurrentProcess())
                File.WriteAllText(PathFor(name), "owner:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks);
            File.WriteAllText(StoppedPathFor(name), "");
        }

        internal static void Cancel(string name)
        {
            File.WriteAllText(PathFor(name), "cancel");
        }

        internal static bool IsRequested(string name)
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return false;
            string value;
            try { value = File.ReadAllText(path).Trim(); }
            catch (IOException) { return false; }
            if (value == "cancel") return true;
            return OwnerGone(value);
        }

        internal static bool OwnerGone(string value)
        {
            var parts = value.Split(':');
            if (parts.Length != 3 || parts[0] != "owner") return false;
            int id;
            long ticks;
            if (!int.TryParse(parts[1], out id) || !long.TryParse(parts[2], out ticks)) return false;
            try
            {
                using (var process = Process.GetProcessById(id))
                    return process.StartTime.ToUniversalTime().Ticks != ticks;
            }
            catch (ArgumentException) { return true; }
            catch (Win32Exception) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        internal static void MarkStopped(string name)
        {
            File.WriteAllText(StoppedPathFor(name), "stopped");
        }

        internal static void WaitStopped(string name)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.ReadAllText(StoppedPathFor(name)).Trim() == "stopped")
                    {
                        Thread.Sleep(500);
                        return;
                    }
                }
                catch (IOException) { }
                Thread.Sleep(250);
            }
        }
    }
}
