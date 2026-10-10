using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    internal sealed class Win7Release
    {
        public Version Version { get; set; }
        public long Size { get; set; }
        public string Digest { get; set; }
        public string Url { get; set; }
    }

    internal static class Win7GitHubUpdater
    {
        private const string AssetName = "ITSeti-Maintenance-Win7-Setup.exe";
        private const string ManifestUrl = "https://raw.githubusercontent.com/izivinizi/IT-Seti_client/main/release-win7.json";
        private const string UpdateTaskName = "ITSeti-Maintenance-Win7-Update";
        private static string WorkerStatusPath { get { return Path.Combine(ScheduledCheckRunner.ResultsRoot, "application-update-status.txt"); } }

        public static async Task<Win7Release> CheckAsync()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            string json;
            using (var client = NewClient())
                json = await client.DownloadStringTaskAsync(new Uri(ManifestUrl));
            return ParseManifest(json, Assembly.GetExecutingAssembly().GetName().Version);
        }

        internal static Win7Release ParseManifest(string json, Version current)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (data == null) throw new InvalidDataException("Манифест Windows 7 пуст.");
            var versionText = Required(data, "version");
            Version version;
            if (!Regex.IsMatch(versionText, @"^\d+\.\d+\.\d+$") || !Version.TryParse(versionText, out version))
                throw new InvalidDataException("Неверная версия в манифесте Windows 7.");
            var tag = "v" + versionText + "-win7-beta";
            var url = "https://github.com/izivinizi/IT-Seti_client/releases/download/" + tag + "/" + AssetName;
            if (Required(data, "tag") != tag || Required(data, "assetName") != AssetName || Required(data, "downloadUrl") != url)
                throw new InvalidDataException("Манифест указывает на неподходящий установщик.");
            var digest = Required(data, "digest");
            long size;
            if (!Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$") ||
                !long.TryParse(Required(data, "size"), out size) || size < 1 || size > 500000000)
                throw new InvalidDataException("Размер или SHA-256 установщика некорректен.");
            return version.CompareTo(new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) > 0
                ? new Win7Release { Version = version, Size = size, Digest = digest.Substring(7), Url = url }
                : null;
        }

        public static int RunBackgroundWorker()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;

            try
            {
                WriteWorkerStatus("Проверка версии приложения на GitHub...");
                var release = CheckAsync().GetAwaiter().GetResult();
                if (release == null)
                {
                    WriteWorkerStatus("Установлена последняя версия Windows 7.");
                    return 0;
                }

                var path = Path.Combine(ScheduledCheckRunner.ResultsRoot, "pending-win7-update.exe");
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                WriteWorkerStatus("Загружается обновление Windows 7 " + release.Version + "...");
                DownloadAndVerifyAsync(release, path, null).GetAwaiter().GetResult();
                WriteWorkerStatus("Подготовлен установщик Windows 7 " + release.Version + "; запускается тихая установка.");
                using (var process = Process.Start(new ProcessStartInfo(path,
                    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path)
                }))
                    if (process == null) throw new InvalidOperationException("Windows не запустила фоновый установщик.");
                return 0;
            }
            catch (Exception ex)
            {
                WriteWorkerStatus("Фоновое обновление не выполнено: " + ex.Message);
                try { File.WriteAllText(Path.Combine(ScheduledCheckRunner.ResultsRoot, "application-update-error.txt"), ex.ToString(), new UTF8Encoding(false)); }
                catch (Exception writeError) when (writeError is IOException || writeError is UnauthorizedAccessException) { }
                return 1;
            }
        }

        public static Task<string> QueueBackgroundCheckAsync()
        {
            return Task.Run(() =>
            {
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
                using (var process = Process.Start(new ProcessStartInfo(executable,
                    "/Run /TN \"" + UpdateTaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("Планировщик задач не запустился.");
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new TimeoutException("Планировщик задач не ответил за 10 секунд.");
                    }
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("Системная задача обновления не зарегистрирована (код " + process.ExitCode + ").");
                }
                return "Проверка обновления приложения выполняется в фоне.";
            });
        }

        public static string ReadBackgroundStatus()
        {
            try { return File.Exists(WorkerStatusPath) ? File.ReadAllText(WorkerStatusPath, Encoding.UTF8).Trim() : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        public static async Task StartInstallAsync(Win7Release release, IProgress<string> progress)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Откройте инженерное окно с правами администратора.");
            var path = Path.Combine(Path.GetTempPath(), "ITSeti-Win7-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                await DownloadAndVerifyAsync(release, path, progress);
                progress.Report("Запуск установщика Windows 7...");
                using (var process = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path)
                }))
                    if (process == null) throw new InvalidOperationException("Windows не запустила установщик.");
            }
            catch
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                throw;
            }
        }

        private static async Task DownloadAndVerifyAsync(Win7Release release, string path, IProgress<string> progress)
        {
            using (var client = NewClient())
            {
                if (progress != null)
                    client.DownloadProgressChanged += (sender, args) =>
                    {
                        if (args.BytesReceived > release.Size) client.CancelAsync();
                        else progress.Report("Загрузка обновления: " + Math.Min(100, 100 * args.BytesReceived / release.Size) + "%");
                    };
                await client.DownloadFileTaskAsync(new Uri(release.Url), path);
            }
            if (progress != null) progress.Report("Проверка размера и SHA-256...");
            if (new FileInfo(path).Length != release.Size)
                throw new InvalidDataException("Размер загруженного файла не совпал с манифестом.");
            string digest;
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
                digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            if (!digest.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 установщика не совпал с манифестом.");
        }

        private static void WriteWorkerStatus(string status)
        {
            var temporary = WorkerStatusPath + ".tmp";
            try
            {
                File.WriteAllText(temporary, status, new UTF8Encoding(false));
                if (File.Exists(WorkerStatusPath)) File.Replace(temporary, WorkerStatusPath, null);
                else File.Move(temporary, WorkerStatusPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static WebClient NewClient()
        {
            var client = new TimeLimitedWebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "ITSeti-Maintenance-Win7/2.3.1";
            client.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return client;
        }

        private sealed class TimeLimitedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                request.Timeout = 15000;
                var http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = 30000;
                return request;
            }
        }

        private static string Required(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
                throw new InvalidDataException("В манифесте Windows 7 отсутствует поле " + key + ".");
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
