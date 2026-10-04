using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class SystemCleanupResult
    {
        public string StartedAtUtc { get; set; }
        public string Summary { get; set; }
        public string Error { get; set; }
    }

    internal static class SystemCleanupTaskRunner
    {
        internal const string TaskName = "ITSeti-Maintenance-Win7-Cleanup-System";
        private const string CacheRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        private static readonly string ResultsRoot = ScheduledCheckRunner.ResultsRoot;
        private static readonly string ResultPath = Path.Combine(ResultsRoot, "system-cleanup.json");
        private static readonly string[] SystemCategories = { "Delivery Optimization Files", "Update Cleanup", "Device Driver Packages" };

        private sealed class SavedValue
        {
            public string KeyName;
            public string FlagName;
            public bool HadValue;
            public object Value;
            public RegistryValueKind Kind;
        }

        public static int RunWorker()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            var result = new SystemCleanupResult { StartedAtUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
            try { result.Summary = CleanSystemFiles(); }
            catch (OperationCanceledException)
            {
                result.Error = "Остановлено пользователем.";
                WriteResult(result);
                return 2;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            WriteResult(result);
            return string.IsNullOrEmpty(result.Error) ? 0 : 1;
        }

        public static string CollectInto(CancellationToken cancellationToken)
        {
            ManualTaskCancellation.Begin("cleanup-system");
            var started = DateTime.UtcNow;
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    "/Run /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Планировщик не запустился.");
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!process.WaitForExit(250) && DateTime.UtcNow < deadline)
                        ThrowIfCancelled(cancellationToken);
                    if (!process.HasExited || process.ExitCode != 0)
                        throw new InvalidOperationException("Системная очистка не запустилась через задачу SYSTEM.");
                }

                var resultDeadline = DateTime.UtcNow.AddMinutes(30);
                while (DateTime.UtcNow < resultDeadline)
                {
                    ThrowIfCancelled(cancellationToken);
                    if (File.Exists(ResultPath) && File.GetLastWriteTimeUtc(ResultPath) >= started)
                    {
                        SystemCleanupResult result;
                        using (var stream = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            result = new JavaScriptSerializer().Deserialize<SystemCleanupResult>(reader.ReadToEnd());
                        if (result == null) throw new InvalidDataException("Задача очистки вернула пустой отчёт.");
                        if (!string.IsNullOrEmpty(result.Error)) throw new InvalidOperationException(result.Error);
                        return result.Summary;
                    }
                    Thread.Sleep(250);
                }
                throw new TimeoutException("Системная очистка не завершилась за 30 минут.");
            }
            catch (OperationCanceledException)
            {
                ManualTaskCancellation.Cancel("cleanup-system");
                ManualTaskCancellation.WaitStopped("cleanup-system");
                throw;
            }
        }

        private static string CleanSystemFiles()
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var drive = new DriveInfo(systemRoot);
            var freeBefore = drive.AvailableFreeSpace;
            var cleanmgr = Path.Combine(Environment.SystemDirectory, "cleanmgr.exe");
            var saved = new List<SavedValue>();
            var selectedNames = new List<string>();
            var skippedNames = new List<string>();
            var number = 0;
            var flag = string.Empty;
            try
            {
                if (!File.Exists(cleanmgr)) return "Штатная очистка системных обновлений недоступна: cleanmgr.exe не найден.";
                using (var cache = Registry.LocalMachine.OpenSubKey(CacheRoot, false))
                {
                    if (cache == null) return "Категории системной очистки Windows не найдены.";
                    var available = new HashSet<string>(cache.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
                    do
                    {
                        number = new Random(Guid.NewGuid().GetHashCode()).Next(1000, 10000);
                        flag = "StateFlags" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    } while (cache.GetSubKeyNames().Any(name =>
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(CacheRoot + "\\" + name, false))
                            return key != null && key.GetValueNames().Any(value => value.Equals(flag, StringComparison.OrdinalIgnoreCase));
                    }));

                    foreach (var name in SystemCategories)
                    {
                        if (!available.Contains(name)) { skippedNames.Add(name); continue; }
                        using (var key = Registry.LocalMachine.OpenSubKey(CacheRoot + "\\" + name, true))
                        {
                            if (key == null) { skippedNames.Add(name); continue; }
                            var state = new SavedValue { KeyName = name, FlagName = flag, HadValue = key.GetValueNames().Contains(flag, StringComparer.OrdinalIgnoreCase) };
                            if (state.HadValue)
                            {
                                state.Value = key.GetValue(flag, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                                state.Kind = key.GetValueKind(flag);
                            }
                            saved.Add(state);
                            key.SetValue(flag, 2, RegistryValueKind.DWord);
                            selectedNames.Add(name);
                        }
                    }
                }
                if (saved.Count == 0)
                    return "В этой версии Windows не обнаружены категории очистки доставки, обновлений или пакетов драйверов.";
                ThrowIfCancelled(null);
                using (var process = Process.Start(new ProcessStartInfo(cleanmgr, "/sagerun:" + number + " /d " + systemRoot.TrimEnd('\\'))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Штатная очистка Windows не запустилась.");
                    var deadline = DateTime.UtcNow.AddMinutes(25);
                    while (!process.WaitForExit(500) && DateTime.UtcNow < deadline)
                    {
                        if (ManualTaskCancellation.IsRequested("cleanup-system"))
                        {
                            try { process.Kill(); } catch (InvalidOperationException) { }
                            try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
                            throw new OperationCanceledException();
                        }
                    }
                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new TimeoutException("Штатная очистка Windows превысила 25 минут.");
                    }
                    if (process.ExitCode != 0) throw new InvalidOperationException("cleanmgr завершился с кодом " + process.ExitCode + ".");
                }
                var delta = Math.Max(0, drive.AvailableFreeSpace - freeBefore) / 1048576.0;
                return "Системная очистка: " + string.Join(", ", selectedNames) + "; освобождено примерно " +
                    delta.ToString("N1") + " МБ (включая параллельные изменения). Не поддерживаются в этой Windows: " +
                    (skippedNames.Count == 0 ? "нет" : string.Join(", ", skippedNames)) + ".";
            }
            finally { RestoreCategories(saved); }
        }

        private static void RestoreCategories(IEnumerable<SavedValue> values)
        {
            foreach (var state in values)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(CacheRoot + "\\" + state.KeyName, true))
                    {
                        if (key == null) continue;
                        if (state.HadValue) key.SetValue(state.FlagName, state.Value, state.Kind);
                        else key.DeleteValue(state.FlagName, false);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            }
        }

        private static void ThrowIfCancelled(CancellationToken? token)
        {
            if ((token.HasValue && token.Value.IsCancellationRequested) || ManualTaskCancellation.IsRequested("cleanup-system"))
                throw new OperationCanceledException();
        }

        private static void WriteResult(SystemCleanupResult result)
        {
            var temporary = ResultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                if (File.Exists(ResultPath)) File.Replace(temporary, ResultPath, null);
                else File.Move(temporary, ResultPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
