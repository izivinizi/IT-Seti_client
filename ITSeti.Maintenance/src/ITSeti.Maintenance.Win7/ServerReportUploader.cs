using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using System.Security.AccessControl;

namespace ITSeti.Maintenance.Win7
{
    internal static class ServerReportUploader
    {
        private static readonly string SharedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        private static readonly string DeviceFile = Path.Combine(SharedRoot, "server-device.json");
        private static readonly string QueueDirectory = Path.Combine(SharedRoot, "ReportQueue");
        private static readonly string ResultRoot = ScheduledCheckRunner.ResultsRoot;

        public static void Queue(LegacySnapshot snapshot)
        {
            if (!File.Exists(DeviceFile)) return;
            Directory.CreateDirectory(QueueDirectory);
            var id = Guid.TryParse(snapshot.Id, out var reportId) ? reportId : Guid.NewGuid();
            snapshot.Id = id.ToString("D");
            if (string.IsNullOrWhiteSpace(snapshot.StartedAt))
                snapshot.StartedAt = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var target = Path.Combine(QueueDirectory, id.ToString("N") + ".json");
            if (File.Exists(target)) return;
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, snapshot.ToJson(), new UTF8Encoding(false));
                try { File.Move(temporary, target); }
                catch (IOException) { if (!File.Exists(target)) throw; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void Trigger()
        {
            if (!File.Exists(DeviceFile) || !Directory.Exists(QueueDirectory)) return;
            try
            {
                using (var process = ProcessStart())
                {
                    if (process == null || !process.WaitForExit(10000) || process.ExitCode != 0)
                        WriteError("Не удалось запустить системную задачу отправки отчётов.");
                }
            }
            catch (Exception ex) { WriteError(ex.GetType().Name + ": " + ex.Message); }
        }

        private static void RefreshProcessPolicy(Uri server, Dictionary<string,object> device, JavaScriptSerializer serializer)
        {
            var path = Path.Combine(SharedRoot, "process-policy.json");
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(new Uri(server, "/api/v1/process-policy"));
                request.AllowAutoRedirect = false;
                request.Timeout = request.ReadWriteTimeout = 5000;
                request.Headers.Add("X-Device-Id", Convert.ToString(device["deviceId"]));
                request.Headers.Add("X-Device-Key", Convert.ToString(device["deviceKey"]));
                string json;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var text = new StringBuilder();
                    var buffer = new char[4096];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (text.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Process policy exceeds 2 MB.");
                        text.Append(buffer, 0, read);
                    }
                    json = text.ToString();
                }
                var policy = serializer.Deserialize<Dictionary<string,object>>(json);
                if (policy == null || !policy.ContainsKey("allowedNames") || !policy.ContainsKey("allowedPublishers")) return;
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true,false);
                foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),FileSystemRights.Read,AccessControlType.Allow));
                File.SetAccessControl(temporary,acl);
                if (File.Exists(path)) File.Replace(temporary,path,null);
                else File.Move(temporary,path);
            }
            catch { /* An offline policy refresh must not interrupt report delivery. */ }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        }

        public static int Upload()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != "S-1-5-18") return 2;
            if (!File.Exists(DeviceFile)) return 0;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var device = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(DeviceFile));
                if (device == null || !device.ContainsKey("deviceId") || !device.ContainsKey("deviceKey") ||
                    !device.ContainsKey("serverUrl")) throw new InvalidDataException("Параметры подключения к серверу повреждены.");
                var server = new Uri(Convert.ToString(device["serverUrl"]));
                if (server.Scheme != Uri.UriSchemeHttps || !server.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Адрес сервера отчётов недействителен.");
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                RefreshProcessPolicy(server, device, serializer);
                if (!Directory.Exists(QueueDirectory)) return 0;
                foreach (var path in Directory.GetFiles(QueueDirectory, "*.json"))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 12 * 1024 * 1024) throw new InvalidDataException("Отчёт превышает лимит сервера 12 МБ.");
                        var request = (HttpWebRequest)WebRequest.Create(new Uri(server, "/api/v1/check-runs"));
                        request.Method = "POST";
                        request.AllowAutoRedirect = false;
                        request.Timeout = 30000;
                        request.ReadWriteTimeout = 30000;
                        request.ContentType = "application/json; charset=utf-8";
                        request.Headers.Add("X-Device-Id", Convert.ToString(device["deviceId"]));
                        request.Headers.Add("X-Device-Key", Convert.ToString(device["deviceKey"]));
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                        using (var response = (HttpWebResponse)request.GetResponse())
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new InvalidOperationException("Сервер ответил HTTP " + (int)response.StatusCode + ".");
                        File.Delete(path);
                    }
                    catch (WebException ex)
                    {
                        using (var response = ex.Response as HttpWebResponse)
                        throw new InvalidOperationException(response == null
                            ? "Сервер отчётов недоступен: " + ex.Status
                            : "Сервер отчётов ответил HTTP " + (int)response.StatusCode + ".", ex);
                    }
                }
                DeleteError();
                return 0;
            }
            catch (Exception ex)
            {
                WriteError(ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static Process ProcessStart()
        {
            return Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                "/Run /TN \"ITSeti-Maintenance-Win7-Upload\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static void WriteError(string message)
        {
            try { Directory.CreateDirectory(ResultRoot); File.WriteAllText(Path.Combine(ResultRoot, "server-upload-error.txt"), message, new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        private static void DeleteError()
        {
            try { File.Delete(Path.Combine(ResultRoot, "server-upload-error.txt")); }
            catch (Exception) { }
        }
    }
}
