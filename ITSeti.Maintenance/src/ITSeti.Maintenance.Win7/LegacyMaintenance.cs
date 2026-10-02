using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class LegacyUpdatePolicy
    {
        public int? NoAutoUpdate { get; set; }
        public int? AuOptions { get; set; }
    }

    internal static class LegacyMaintenance
    {
        private const string UpdateKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSetiMaintenanceWin7", "windows-updates-before.json");

        public static string CleanCurrentUser()
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar);
            if (!temp.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Временная папка находится вне профиля текущего пользователя. Очистка отменена.");
            long freed = 0;
            var removed = 0;
            foreach (var path in Directory.GetFiles(temp, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-2)) continue;
                    var bytes = file.Length;
                    file.Delete();
                    freed += bytes;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return "Временные файлы текущего пользователя: удалено " + removed + ", освобождено " +
                (freed / 1048576.0).ToString("N1") + " МБ. Файлы моложе двух дней и вложенные папки не тронуты.";
        }

        public static string CheckWindowsUpdates()
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", true);
            dynamic session = Activator.CreateInstance(type);
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            var count = (int)result.Updates.Count;
            return count == 0 ? "Доступных обновлений Windows не найдено." : "Доступно обновлений Windows: " + count + ". Установка не запускается.";
        }

        public static string DisableAutomaticUpdates()
        {
            if (!File.Exists(StatePath))
            {
                using (var current = Registry.LocalMachine.OpenSubKey(UpdateKey))
                {
                    var old = new LegacyUpdatePolicy { NoAutoUpdate = ReadDword(current, "NoAutoUpdate"), AuOptions = ReadDword(current, "AUOptions") };
                    File.WriteAllText(StatePath, new JavaScriptSerializer().Serialize(old), new UTF8Encoding(false));
                }
            }
            using (var key = Registry.LocalMachine.CreateSubKey(UpdateKey))
            {
                if (key == null) throw new InvalidOperationException("Не удалось открыть политику Windows Update.");
                key.SetValue("NoAutoUpdate", 1, RegistryValueKind.DWord);
            }
            return "Автоматическая установка обновлений отключена. Ручная проверка остаётся доступна.";
        }

        public static string RestoreAutomaticUpdates()
        {
            if (!File.Exists(StatePath)) return "Предыдущая настройка не сохранена; менять политику нельзя.";
            var old = new JavaScriptSerializer().Deserialize<LegacyUpdatePolicy>(File.ReadAllText(StatePath));
            if (old == null) throw new InvalidDataException("Сохранённая настройка обновлений повреждена.");
            using (var key = Registry.LocalMachine.CreateSubKey(UpdateKey))
            {
                if (key == null) throw new InvalidOperationException("Не удалось открыть политику Windows Update.");
                RestoreDword(key, "NoAutoUpdate", old.NoAutoUpdate);
                RestoreDword(key, "AUOptions", old.AuOptions);
            }
            File.Delete(StatePath);
            return "Прежняя политика автоматических обновлений восстановлена.";
        }

        private static int? ReadDword(RegistryKey key, string name)
        {
            if (key == null || key.GetValueKindSafe(name) != RegistryValueKind.DWord) return null;
            return Convert.ToInt32(key.GetValue(name));
        }

        private static void RestoreDword(RegistryKey key, string name, int? value)
        {
            if (value.HasValue) key.SetValue(name, value.Value, RegistryValueKind.DWord);
            else key.DeleteValue(name, false);
        }

        public static string RunSfc()
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSetiMaintenanceWin7", "sfc-last.txt");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sfc.exe"), "/scannow")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("SFC не запустился.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(90 * 60 * 1000)) throw new TimeoutException("SFC выполняется дольше 90 минут.");
                File.WriteAllText(log, output + Environment.NewLine + error, Encoding.Unicode);
                return "SFC завершился с кодом " + process.ExitCode + ". Журнал: " + log;
            }
        }

        private static RegistryValueKind? GetValueKindSafe(this RegistryKey key, string name)
        {
            try { return key.GetValueKind(name); }
            catch (IOException) { return null; }
        }
    }
}
