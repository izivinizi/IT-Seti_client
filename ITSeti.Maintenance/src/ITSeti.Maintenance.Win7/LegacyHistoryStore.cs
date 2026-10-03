using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    internal static class LegacyHistoryStore
    {
        public static void Save(string root, LegacySnapshot snapshot)
        {
            var directory = Path.Combine(root, "History");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            File.WriteAllText(path, snapshot.ToJson(), new UTF8Encoding(false));
            foreach (var old in Directory.GetFiles(directory, "*.json").OrderByDescending(x => x).Skip(30))
                File.Delete(old);
        }

        public static List<LegacySnapshot> Load(string root)
        {
            var directory = Path.Combine(root, "History");
            if (!Directory.Exists(directory)) return new List<LegacySnapshot>();
            var results = new List<LegacySnapshot>();
            foreach (var path in Directory.GetFiles(directory, "*.json").OrderByDescending(x => x).Take(30))
            {
                try
                {
                    var value = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                        .Deserialize<LegacySnapshot>(File.ReadAllText(path));
                    if (value != null) results.Add(value);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ArgumentException) { }
            }
            return results;
        }

        public static string Compare(LegacySnapshot current, LegacySnapshot previous)
        {
            if (previous == null) return "Предыдущей проверки для сравнения нет.";
            var lines = new List<string>();
            var currentDisk = current.Disks.FirstOrDefault(x => x.IsSystem);
            var previousDisk = previous.Disks.FirstOrDefault(x => x.IsSystem);
            if (currentDisk != null && previousDisk != null)
                lines.Add(string.Format("Свободно на системном диске: {0:N1} → {1:N1} ГБ.",
                    previousDisk.FreeBytes / 1073741824.0, currentDisk.FreeBytes / 1073741824.0));
            if (current.Benchmark != null && previous.Benchmark != null &&
                current.Benchmark.ReadMbps.HasValue && previous.Benchmark.ReadMbps.HasValue)
                lines.Add(string.Format("Чтение диска: {0:N1} → {1:N1} МБ/с; запись: {2:N1} → {3:N1} МБ/с.",
                    previous.Benchmark.ReadMbps, current.Benchmark.ReadMbps,
                    previous.Benchmark.WriteMbps, current.Benchmark.WriteMbps));
            var oldEvents = new HashSet<string>(previous.Events.Select(x => x.Log + "|" + x.Source + "|" + x.Id), StringComparer.OrdinalIgnoreCase);
            var newEvents = current.Events.Where(x => !oldEvents.Contains(x.Log + "|" + x.Source + "|" + x.Id))
                .Select(x => x.Source + " #" + x.Id).Distinct().Take(5).ToArray();
            lines.Add("Новые типы ошибок: " + (newEvents.Length == 0 ? "нет" : string.Join(", ", newEvents)) + ".");
            var oldProcesses = new HashSet<string>(previous.Processes.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var newProcesses = current.Processes.Where(x => !oldProcesses.Contains(x.Name))
                .Select(x => x.Name).Distinct().Take(8).ToArray();
            lines.Add("Новые процессы: " + (newProcesses.Length == 0 ? "нет" : string.Join(", ", newProcesses)) + ".");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
