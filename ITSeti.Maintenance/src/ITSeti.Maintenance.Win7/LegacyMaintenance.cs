using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Security.Principal;

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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        public static string CleanCurrentUser()
        {
            return CleanCurrentUser(System.Threading.CancellationToken.None);
        }

        public static string CleanCurrentUser(System.Threading.CancellationToken cancellationToken)
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar);
            if (!temp.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Временная папка находится вне профиля текущего пользователя. Очистка отменена.");
            long freed = 0;
            var removed = 0;
            var skipped = 0;
            var categories = new List<string>();
            CleanDirectoryContents(temp, DateTime.UtcNow.AddDays(-2), cancellationToken, ref freed, ref removed, ref skipped);
            categories.Add("временные файлы");

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var thumbnailCache = Path.Combine(local, "Microsoft", "Windows", "Explorer");
            var thumbnailCount = DeleteFiles(thumbnailCache, new[] { "thumbcache_*.db", "iconcache_*.db" }, cancellationToken, ref freed, ref removed, ref skipped);
            if (Directory.Exists(thumbnailCache)) categories.Add("кэш эскизов (" + thumbnailCount + " файлов)");
            else categories.Add("кэш эскизов недоступен в этой версии Windows");

            var internetRoots = new[] {
                new { Name = "Internet Cache Files", Path = Path.Combine(local, "Microsoft", "Windows", "Temporary Internet Files") },
                new { Name = "Internet Cache", Path = Path.Combine(local, "Microsoft", "Windows", "INetCache") }
            };
            foreach (var item in internetRoots)
            {
                if (!Directory.Exists(item.Path)) { categories.Add(item.Name + " недоступен в этой версии Windows"); continue; }
                CleanDirectoryContents(item.Path, DateTime.MaxValue, cancellationToken, ref freed, ref removed, ref skipped);
                categories.Add(item.Name);
            }

            var shaderCache = Path.Combine(local, "D3DSCache");
            if (Directory.Exists(shaderCache))
            {
                CleanDirectoryContents(shaderCache, DateTime.MaxValue, cancellationToken, ref freed, ref removed, ref skipped);
                categories.Add("кэш шейдеров Direct3D");
            }
            else categories.Add("кэш Direct3D недоступен в этой версии Windows");

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                categories.Add("корзина текущего пользователя");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is COMException)
            {
                categories.Add("корзина: не удалось очистить (" + ex.Message + ")");
            }

            return "Очистка текущего пользователя (" + WindowsIdentity.GetCurrent().Name + "): " +
                string.Join("; ", categories) + ". Удалено файлов: " + removed +
                ", освобождено " + (freed / 1048576.0).ToString("N1") + " МБ; пропущено занятых/недоступных: " + skipped + ".";
        }

        private static int DeleteFiles(string root, IEnumerable<string> patterns, System.Threading.CancellationToken token,
            ref long freed, ref int removed, ref int skipped)
        {
            if (!Directory.Exists(root)) return 0;
            var count = 0;
            foreach (var pattern in patterns)
                foreach (var file in Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        var info = new FileInfo(file);
                        var length = info.Length;
                        info.Delete();
                        freed += length;
                        removed++;
                        count++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { skipped++; }
                }
            return count;
        }

        private static void CleanDirectoryContents(string root, DateTime newerThanUtc, System.Threading.CancellationToken token,
            ref long freed, ref int removed, ref int skipped)
        {
            if (!Directory.Exists(root)) return;
            var directories = new List<string>();
            var pending = new System.Collections.Generic.Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var path in entries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Add(path);
                            pending.Push(path);
                            continue;
                        }
                        var file = new FileInfo(path);
                        if (file.LastWriteTimeUtc >= newerThanUtc) continue;
                        var bytes = file.Length;
                        file.Delete();
                        freed += bytes;
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { skipped++; }
                }
            }
            foreach (var directory in directories.OrderByDescending(x => x.Length))
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { skipped++; }
        }

        public static string CleanupAfterFullCheck(string originalUserSid, System.Threading.CancellationToken cancellationToken)
        {
            var results = new System.Collections.Generic.List<string>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(originalUserSid))
                    results.Add("Профиль пользователя не очищен: исходная учётная запись недоступна.");
                else if (string.Equals(UserProfileCleanupBridge.CurrentUserSid, originalUserSid, StringComparison.OrdinalIgnoreCase))
                    results.Add(CleanCurrentUser(cancellationToken));
                else results.Add(UserProfileCleanupBridge.Clean(originalUserSid, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add("Профиль пользователя не очищен: " + ex.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(SystemCleanupTaskRunner.CollectInto(cancellationToken));
            return string.Join(" ", results);
        }

        public static string CleanupAfterFullCheck(bool cleanupCurrentUser, System.Threading.CancellationToken cancellationToken)
        {
            return CleanupAfterFullCheck(cleanupCurrentUser ? UserProfileCleanupBridge.CurrentUserSid : null, cancellationToken);
        }

        public static string CleanupAllForCurrentUser()
        {
            return CleanupAfterFullCheck(UserProfileCleanupBridge.CurrentUserSid, System.Threading.CancellationToken.None);
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
