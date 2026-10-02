using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

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
        private const string ManifestUrl = "https://raw.githubusercontent.com/izivinizi/IT-Seti_client/beta/release-win7.json";

        public static async Task<Win7Release> CheckAsync()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            string json;
            using (var client = NewClient())
                json = await client.DownloadStringTaskAsync(new Uri(ManifestUrl));
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
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            return version.CompareTo(new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) > 0
                ? new Win7Release { Version = version, Size = size, Digest = digest.Substring(7), Url = url }
                : null;
        }

        public static async Task StartInstallAsync(Win7Release release, IProgress<string> progress)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Откройте инженерное окно с правами администратора.");
            var path = Path.Combine(Path.GetTempPath(), "ITSeti-Win7-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var client = NewClient())
                {
                    client.DownloadProgressChanged += (sender, args) =>
                    {
                        if (args.BytesReceived > release.Size) client.CancelAsync();
                        else progress.Report("Загрузка обновления: " + Math.Min(100, 100 * args.BytesReceived / release.Size) + "%");
                    };
                    await client.DownloadFileTaskAsync(new Uri(release.Url), path);
                }
                progress.Report("Проверка размера и SHA-256...");
                if (new FileInfo(path).Length != release.Size)
                    throw new InvalidDataException("Размер загруженного файла не совпал с манифестом.");
                string digest;
                using (var hash = SHA256.Create())
                using (var stream = File.OpenRead(path))
                    digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
                if (!digest.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 установщика не совпал с манифестом.");
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
                try { File.Delete(path); } catch (IOException) { }
                throw;
            }
        }

        private static WebClient NewClient()
        {
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "ITSeti-Maintenance-Win7/1.2.1";
            return client;
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
