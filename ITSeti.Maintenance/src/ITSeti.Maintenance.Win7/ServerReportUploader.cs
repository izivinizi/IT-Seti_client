using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using System.Security.AccessControl;
using System.Threading;

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
                var serializer=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
                var report=serializer.Deserialize<Dictionary<string,object>>(snapshot.ToJson());
                var errors=LegacyApplicationErrors.Read().Take(20).Select(error=>new {error.Id,error.OccurredAt,error.Component,error.Operation,error.Summary,error.ExceptionType}).ToArray();
                report["HasApplicationErrors"]=errors.Length>0;
                report["ApplicationErrors"]=errors;
                if(snapshot.Kind != "Постоянный мониторинг")
                {
                    foreach (var field in LegacyDiagnosticAdapter.ReportFields(snapshot)) report[field.Key] = field.Value;
                }
                else report["DiagnosticIssues"]=snapshot.Findings.Select(finding=>new {
                    Title=System.Text.RegularExpressions.Regex.Replace(finding,@"\s+[-+]?\d+(?:[.,]\d+)?\s*(?:°C|%|В|ГБ)\.?\s*$",""),Detail=finding,Severity="Critical"
                }).ToArray();
                File.WriteAllText(temporary, serializer.Serialize(report), new UTF8Encoding(false));
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

        private static readonly string LastUploadAttemptPath = Path.Combine(SharedRoot, "report-upload-last-attempt.txt");
        private static readonly string UploadMutexName = @"Global\ITSeti-Maintenance-ReportUpload";

        public static bool HasPendingReports()
        {
            return Directory.Exists(QueueDirectory) && Directory.GetFiles(QueueDirectory, "*.json").Length > 0;
        }

        public static bool IsUploadDue()
        {
            DateTime lastAttempt;
            return !DateTime.TryParse(File.Exists(LastUploadAttemptPath) ? File.ReadAllText(LastUploadAttemptPath) : null,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastAttempt) ||
                DateTime.UtcNow - lastAttempt.ToUniversalTime() >= TimeSpan.FromHours(1);
        }

        private static bool TryClaimUploadWindow()
        {
            using (var mutex = new Mutex(false, UploadMutexName))
            {
                var locked = false;
                try
                {
                    try { locked = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked || !IsUploadDue()) return false;
                    Directory.CreateDirectory(SharedRoot);
                    var temporary = LastUploadAttemptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), new UTF8Encoding(false));
                        if (File.Exists(LastUploadAttemptPath)) File.Replace(temporary, LastUploadAttemptPath, null);
                        else File.Move(temporary, LastUploadAttemptPath);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return true;
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        private static void SaveSoftwareCatalogResponse(HttpWebResponse response, JavaScriptSerializer serializer, bool rawCatalog = false)
        {
            if (response.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Каталог ПО из ответа сервера превышает 2 МБ.");
            string json;
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8, true))
            {
                var text = new StringBuilder();
                var buffer = new char[4096];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Каталог ПО из ответа сервера превышает 2 МБ.");
                    text.Append(buffer, 0, read);
                }
                json = text.ToString();
            }
            if (string.IsNullOrWhiteSpace(json)) return;
            object value;
            if (rawCatalog) value = serializer.DeserializeObject(json);
            else
            {
                var payload = serializer.Deserialize<Dictionary<string, object>>(json);
                if (payload == null || !payload.TryGetValue("softwareCatalog", out value) || value == null) return;
            }
            var items = value as object[];
            if (items == null && value is System.Collections.ArrayList array) items = array.ToArray();
            if (items == null) throw new InvalidDataException("Ответ сервера содержит некорректный каталог ПО.");
            foreach (var item in items)
            {
                var package = item as Dictionary<string, object>;
                if (package == null) throw new InvalidDataException("Ответ сервера содержит некорректную запись каталога ПО.");
                object idValue, keyValue, platformValue, hashValue, sizeValue, fileValue;
                if (!package.TryGetValue("id", out idValue) || !Guid.TryParse(Convert.ToString(idValue, CultureInfo.InvariantCulture), out var packageId) || packageId == Guid.Empty ||
                    !package.TryGetValue("key", out keyValue) || string.IsNullOrWhiteSpace(Convert.ToString(keyValue, CultureInfo.InvariantCulture)) ||
                    !package.TryGetValue("platform", out platformValue) || !new[] { "windows", "win7", "any" }.Contains(Convert.ToString(platformValue, CultureInfo.InvariantCulture)) ||
                    !package.TryGetValue("sha256", out hashValue) || !System.Text.RegularExpressions.Regex.IsMatch(Convert.ToString(hashValue, CultureInfo.InvariantCulture), "^[a-fA-F0-9]{64}$") ||
                    !package.TryGetValue("sizeBytes", out sizeValue) || !package.TryGetValue("fileName", out fileValue) ||
                    string.IsNullOrWhiteSpace(Convert.ToString(fileValue, CultureInfo.InvariantCulture)) ||
                    Path.GetFileName(Convert.ToString(fileValue, CultureInfo.InvariantCulture)) != Convert.ToString(fileValue, CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Ответ сервера содержит запись каталога ПО с недопустимыми полями.");
                long sizeBytes;
                try { sizeBytes = Convert.ToInt64(sizeValue, CultureInfo.InvariantCulture); }
                catch (Exception) { throw new InvalidDataException("Размер записи каталога ПО некорректен."); }
                if (sizeBytes <= 0 || sizeBytes > 1073741824L)
                    throw new InvalidDataException("Размер записи каталога ПО выходит за допустимые пределы.");
            }
            var cache = Path.Combine(SharedRoot, "software-catalog.json");
            var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                File.WriteAllText(temporary, serializer.Serialize(items), new UTF8Encoding(false));
                if (File.Exists(cache)) File.Replace(temporary, cache, null);
                else File.Move(temporary, cache);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void RefreshSoftwareCatalog(Uri server, Dictionary<string, object> device, JavaScriptSerializer serializer)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(server, "/api/v1/updates"));
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "ITSeti-Maintenance-Win7/2.4.9";
            request.Headers.Add("X-Device-Id", Convert.ToString(device["deviceId"]));
            request.Headers.Add("X-Device-Key", Convert.ToString(device["deviceKey"]));
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Сервер ответил HTTP " + (int)response.StatusCode + " при получении каталога ПО.");
                SaveSoftwareCatalogResponse(response, serializer, true);
            }
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
                if (!TryClaimUploadWindow()) return 0;
                if (!HasPendingReports())
                {
                    RefreshSoftwareCatalog(server, device, serializer);
                    DeleteError();
                    return 0;
                }
                RefreshProcessPolicy(server, device, serializer);
                var catalogRequested = false;
                var catalogWarnings = new List<string>();
                foreach (var path in Directory.GetFiles(QueueDirectory, "*.json"))
                {
                    var includeCatalog = !catalogRequested;
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 12 * 1024 * 1024) throw new InvalidDataException("Отчёт превышает лимит сервера 12 МБ.");
                        var route = includeCatalog ? "/api/v1/check-runs?includeCatalog=true" : "/api/v1/check-runs?includeCatalog=false";
                        var request = (HttpWebRequest)WebRequest.Create(new Uri(server, route));
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
                        {
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new InvalidOperationException("Сервер ответил HTTP " + (int)response.StatusCode + ".");
                            catalogRequested = true;
                            try { SaveSoftwareCatalogResponse(response, serializer); }
                            catch (Exception ex) { catalogWarnings.Add(ex.GetType().Name + ": " + ex.Message); }
                        }
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
                if (catalogWarnings.Count > 0) WriteError("Отчёт принят, но каталог ПО не сохранён: " + string.Join("; ", catalogWarnings));
                else DeleteError();
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
