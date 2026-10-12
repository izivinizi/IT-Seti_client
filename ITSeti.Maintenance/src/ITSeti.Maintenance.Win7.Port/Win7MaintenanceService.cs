using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    internal sealed class Win7ServiceStatus
    {
        public string State { get; set; }
        public string Operation { get; set; }
        public string RunId { get; set; }
        public string StartedAtUtc { get; set; }
        public string FinishedAtUtc { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public string ResultPath { get; set; }
    }

    internal sealed class Win7ServiceCallResult
    {
        public bool Connected { get; private set; }
        public bool Accepted { get; private set; }
        public string Message { get; private set; }
        public string RunId { get; private set; }

        public Win7ServiceCallResult(bool connected, bool accepted, string message, string runId = null)
        {
            Connected = connected;
            Accepted = accepted;
            Message = message;
            RunId = runId;
        }

        public static Win7ServiceCallResult Unavailable(string message)
        {
            return new Win7ServiceCallResult(false, false, message);
        }
    }

    internal static class Win7MaintenanceServiceHost
    {
        internal const string ServiceName = "ITSetiMaintenanceWin7";
        internal const string PipeName = "ITSeti.Maintenance.Win7.v1";
        internal static readonly string ResultsRoot = ScheduledCheckRunner.ResultsRoot;
        internal static readonly string ServiceRoot = Path.Combine(ResultsRoot, "Service");
        internal static readonly string StatusPath = Path.Combine(ServiceRoot, "status.json");
        internal static readonly string MonitoringPath = Path.Combine(ServiceRoot, "monitoring.json");
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        internal static void Run()
        {
            ServiceBase.Run(new ServiceBase[] { new Win7MaintenanceService() });
        }

        internal static Win7ServiceStatus ReadStatus()
        {
            try
            {
                if (!File.Exists(StatusPath)) return null;
                using (var stream = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return Serializer.Deserialize<Win7ServiceStatus>(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        internal static string ResultPath { get { return Path.Combine(ResultsRoot, "latest.json"); } }

        private sealed class Win7MaintenanceService : ServiceBase
        {
            private static readonly object ErrorGate = new object();
            private static readonly Dictionary<string, DateTime> RecentErrors = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            private readonly object gate = new object();
            private CancellationTokenSource stop;
            private CancellationTokenSource activeRun;
            private string activeRunOwnerSid;
            private Task pipeTask;
            private Task monitorTask;
            private NamedPipeServerStream activePipe;
            private readonly Queue<LegacySnapshot> monitoringWindow = new Queue<LegacySnapshot>();
            private readonly HashSet<string> reportedCritical=new HashSet<string>();
            private DateTime nextSmartAttempt;

            public Win7MaintenanceService()
            {
                ServiceName = Win7MaintenanceServiceHost.ServiceName;
                CanStop = true;
                CanShutdown = true;
                AutoLog = true;
            }

            protected override void OnStart(string[] args)
            {
                Directory.CreateDirectory(ServiceRoot);
                stop = new CancellationTokenSource();
                WriteStatus(new Win7ServiceStatus { State = "Starting", Message = "Служба запускается.", StartedAtUtc = UtcNow() });
                pipeTask = Task.Run(() => PipeLoop(stop.Token));
                monitorTask = Task.Run(() => MonitorLoop(stop.Token));
                WriteStatus(new Win7ServiceStatus { State = "Idle", Message = "Служба обслуживания работает.", StartedAtUtc = UtcNow() });
            }

            protected override void OnStop()
            {
                if (stop == null) return;
                stop.Cancel();
                lock (gate)
                {
                    if (activeRun != null) activeRun.Cancel();
                    if (activePipe != null) activePipe.Dispose();
                }
                try { Task.WaitAll(new[] { pipeTask, monitorTask }, 10000); } catch (Exception) { }
                LegacyHwInfoReader.StopSession();
                stop.Dispose();
                stop = null;
            }

            protected override void OnShutdown()
            {
                OnStop();
                base.OnShutdown();
            }

            private async Task PipeLoop(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    NamedPipeServerStream pipe = null;
                    try
                    {
                        pipe = CreatePipe();
                        lock (gate) activePipe = pipe;
                        var wait = pipe.WaitForConnectionAsync();
                        var completed = await Task.WhenAny(wait, Task.Delay(Timeout.Infinite, cancellationToken));
                        if (completed != wait) break;
                        await HandleClient(pipe, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                    {
                        if (!cancellationToken.IsCancellationRequested) WriteError(ex);
                    }
                    finally
                    {
                        lock (gate) if (ReferenceEquals(activePipe, pipe)) activePipe = null;
                        if (pipe != null) pipe.Dispose();
                    }
                }
            }

            private static NamedPipeServerStream CreatePipe()
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
            }

            private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken cancellationToken)
            {
                using (var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    var read = Task.Run(() => ReadBoundedRequest(reader));
                    if (await Task.WhenAny(read, Task.Delay(5000, cancellationToken)) != read) return;
                    var response = HandleRequest(read.Result, pipe);
                    await writer.WriteLineAsync(Serializer.Serialize(response));
                }
            }

            private Dictionary<string, object> HandleRequest(string line, NamedPipeServerStream pipe)
            {
                if (string.IsNullOrWhiteSpace(line)) return Response(false, "Пустой запрос.");
                Dictionary<string, object> request;
                try { request = Serializer.Deserialize<Dictionary<string, object>>(line); }
                catch (Exception ex) { return Response(false, "Некорректный запрос: " + ex.Message); }
                if (request == null) return Response(false, "Некорректный запрос.");
                var command = Value(request, "command");
                var operation = Value(request, "operation");
                if (string.Equals(command, "ping", StringComparison.OrdinalIgnoreCase))
                    return Response(true, "Служба обслуживания Windows 7 работает.");
                if (string.Equals(command, "start", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(operation, "full", StringComparison.OrdinalIgnoreCase)) return StartRun(true, ClientSid(pipe));
                    if (string.Equals(operation, "quick", StringComparison.OrdinalIgnoreCase)) return StartRun(true, ClientSid(pipe));
                    if (string.Equals(operation, "cleanup", StringComparison.OrdinalIgnoreCase)) return StartCleanup();
                    if (string.Equals(operation, "repair", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsAdministrator(pipe)) return Response(false, "Восстановление доступно только инженеру.");
                        return StartRepair();
                    }
                }
                if (string.Equals(command, "launch", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsAdministrator(pipe)) return Response(false, "Запуск утилит доступен только инженеру.");
                    try { return LaunchInteractiveTool(request); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                        ex is InvalidOperationException || ex is ArgumentException || ex is Win32Exception)
                    { return Response(false, ex.Message); }
                }
                if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
                    return StopRun(pipe);
                return Response(false, "Операция не поддерживается службой Windows 7.");
            }

            private Dictionary<string, object> StartRun(bool full, string ownerSid)
            {
                if (string.IsNullOrWhiteSpace(ownerSid)) return Response(false, "Не удалось определить пользователя, запустившего проверку.");
                lock (gate)
                {
                    full = true;
                    if (activeRun != null)
                        return Response(false, "Проверка уже выполняется.");
                    activeRun = new CancellationTokenSource();
                    activeRunOwnerSid = ownerSid;
                    var token = activeRun.Token;
                    var operation = full ? "FullCheck" : "QuickCheck";
                    var runId = Guid.NewGuid().ToString("D");
                    WriteStatus(new Win7ServiceStatus { State = "Running", Operation = operation, RunId = runId,
                        StartedAtUtc = UtcNow(), Message = "Проверка выполняется службой SYSTEM." });
                    Task.Run(() => ExecuteCheck(full, runId, token));
                    return Response(true, "Проверка запущена службой SYSTEM.", runId);
                }
            }

            private static string ReadBoundedRequest(StreamReader reader)
            {
                var value=new StringBuilder();int character;
                while((character=reader.Read())>=0){if(character=='\n')return value.ToString();if(value.Length>=4096)throw new InvalidDataException("Запрос превышает 4096 символов.");value.Append((char)character);}return value.ToString();
            }

            private Dictionary<string, object> StartCleanup()
            {
                lock (gate)
                {
                    if (activeRun != null)
                        return Response(false, "Другая операция обслуживания уже выполняется.");
                    activeRun = new CancellationTokenSource();
                    var token = activeRun.Token;
                    var runId = Guid.NewGuid().ToString("D");
                    WriteStatus(new Win7ServiceStatus { State = "Running", Operation = "Cleanup", RunId = runId,
                        StartedAtUtc = UtcNow(), Message = "Очистка выполняется службой SYSTEM." });
                    Task.Run(() => ExecuteCleanup(runId, token));
                    return Response(true, "Очистка запущена службой SYSTEM.", runId);
                }
            }

            private Dictionary<string, object> StartRepair()
            {
                lock (gate)
                {
                    if (activeRun != null)
                        return Response(false, "Другая операция обслуживания уже выполняется.");
                    activeRun = new CancellationTokenSource();
                    var token = activeRun.Token;
                    var runId = Guid.NewGuid().ToString("D");
                    WriteStatus(new Win7ServiceStatus { State = "Running", Operation = "Repair", RunId = runId,
                        StartedAtUtc = UtcNow(), Message = "Восстановление выполняется службой SYSTEM." });
                    Task.Run(() => ExecuteRepair(runId, token));
                    return Response(true, "Восстановление запущено службой SYSTEM.", runId);
                }
            }

            private Dictionary<string, object> StopRun(NamedPipeServerStream pipe)
            {
                var callerSid = ClientSid(pipe);
                var administrator = IsAdministrator(pipe);
                lock (gate)
                {
                    if (activeRun == null || activeRun.IsCancellationRequested) return Response(true, "Активная операция не найдена.");
                    if (!administrator && !string.Equals(callerSid, activeRunOwnerSid, StringComparison.OrdinalIgnoreCase))
                        return Response(false, "Остановить проверку может только запустивший её пользователь или администратор.");
                    activeRun.Cancel();
                    return Response(true, "Операция остановлена.");
                }
            }

            private Dictionary<string, object> LaunchInteractiveTool(Dictionary<string, object> request)
            {
                var target = Value(request, "target");
                var argument = Value(request, "argument");
                var sessionText = Value(request, "sessionId");
                int sessionId;
                if (!int.TryParse(sessionText, out sessionId) || sessionId <= 0)
                    return Response(false, "Не удалось определить пользовательский сеанс Windows.");
                Win7ToolSpec spec;
                var operation = Value(request, "operation");
                if (string.Equals(operation, "system", StringComparison.OrdinalIgnoreCase))
                    spec = Win7ToolSpec.FromSystem(target);
                else if (string.Equals(operation, "utility", StringComparison.OrdinalIgnoreCase))
                    spec = Win7ToolSpec.Utility(target, argument);
                else return Response(false, "Неизвестный тип утилиты.");
                var processId = Win7InteractiveProcessLauncher.LaunchAsSystem(sessionId, spec.FileName, spec.Arguments, spec.WorkingDirectory);
                return Response(true, "Утилита запущена службой SYSTEM (PID " + processId + ").");
            }

            private void ExecuteCheck(bool full, string runId, CancellationToken token)
            {
                try
                {
                    var snapshot = LegacyDiagnostics.Collect(full, token);
                    LegacyHwInfoReader.Apply(snapshot);
                    SaveSnapshot(snapshot);
                    WriteStatus(new Win7ServiceStatus { State = "Completed", Operation = full ? "FullCheck" : "QuickCheck", RunId = runId,
                        StartedAtUtc = snapshot.StartedAt, FinishedAtUtc = UtcNow(), Message = "Проверка завершена службой SYSTEM.", ResultPath = ResultPath });
                }
                catch (OperationCanceledException)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Stopped", RunId = runId, FinishedAtUtc = UtcNow(), Message = "Проверка остановлена." });
                }
                catch (Exception ex)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Failed", RunId = runId, FinishedAtUtc = UtcNow(), Error = ex.Message, Message = "Проверка не завершена." });
                }
                finally { FinishRun(); }
            }

            private void ExecuteCleanup(string runId, CancellationToken token)
            {
                try
                {
                    var summary = LegacyMaintenance.CleanupAfterFullCheck(UserProfileCleanupBridge.ActiveConsoleUserSid(), token);
                    WriteStatus(new Win7ServiceStatus { State = "Completed", Operation = "Cleanup", RunId = runId,
                        FinishedAtUtc = UtcNow(), Message = summary });
                }
                catch (OperationCanceledException)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Stopped", Operation = "Cleanup", RunId = runId, FinishedAtUtc = UtcNow(), Message = "Очистка остановлена." });
                }
                catch (Exception ex)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Failed", Operation = "Cleanup", RunId = runId, FinishedAtUtc = UtcNow(), Error = ex.Message, Message = "Очистка не завершена." });
                }
                finally { FinishRun(); }
            }

            private void ExecuteRepair(string runId, CancellationToken token)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var message = LegacyMaintenance.RunSfc();
                    WriteStatus(new Win7ServiceStatus { State = "Completed", Operation = "Repair", RunId = runId, FinishedAtUtc = UtcNow(), Message = message });
                }
                catch (OperationCanceledException)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Stopped", Operation = "Repair", RunId = runId, FinishedAtUtc = UtcNow(), Message = "Восстановление остановлено." });
                }
                catch (Exception ex)
                {
                    WriteStatus(new Win7ServiceStatus { State = "Failed", Operation = "Repair", RunId = runId, FinishedAtUtc = UtcNow(), Error = ex.Message, Message = "Восстановление не завершено." });
                }
                finally { FinishRun(); }
            }

            private void FinishRun()
            {
                lock (gate)
                {
                    if (activeRun != null) { activeRun.Dispose(); activeRun = null; }
                    activeRunOwnerSid = null;
                }
            }

            private static void SaveSnapshot(LegacySnapshot snapshot)
            {
                Directory.CreateDirectory(ResultsRoot);
                WriteAtomically(Path.Combine(ResultsRoot, "latest.txt"), snapshot.ToReport());
                LegacyHistoryStore.Save(ResultsRoot, snapshot);
                WriteAtomically(ResultPath, snapshot.ToJson());
                try { ServerReportUploader.Queue(snapshot); if (ServerReportUploader.Upload() != 0) ServerReportUploader.Trigger(); }
                catch (Exception) { try { ServerReportUploader.Trigger(); } catch (Exception) { } }
            }

            private async Task MonitorLoop(CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        bool idle;
                        lock (gate) idle = activeRun == null;
                        if (idle)
                        {
                            if(DateTime.UtcNow>=nextSmartAttempt){nextSmartAttempt=DateTime.UtcNow.AddHours(1);try { RunDailySmart(); } catch (Exception ex) { WriteError(ex); }}
                            try { CollectMonitoring(); } catch (Exception ex) { WriteError(ex); }
                        }
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }

            private static void RunDailySmart()
            {
                var stampPath = Path.Combine(ServiceRoot, "smart-last-run.txt");
                DateTime stamp;
                if (File.Exists(stampPath) && DateTime.TryParse(File.ReadAllText(stampPath), null, System.Globalization.DateTimeStyles.RoundtripKind, out stamp) &&
                    DateTime.UtcNow < stamp.ToUniversalTime().AddHours(10)) return;
                if (SmartTaskRunner.RunWorker(scheduled: true) == 0)
                    WriteAtomically(stampPath, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }

            private void CollectMonitoring()
            {
                var snapshot = LegacyDiagnostics.CollectMonitoring(CancellationToken.None);
                LegacyHwInfoReader.Apply(snapshot);
                WriteAtomically(MonitoringPath, snapshot.ToJson());

                monitoringWindow.Enqueue(snapshot);
                while (monitoringWindow.Count > 6) monitoringWindow.Dequeue();
                var critical = snapshot.Findings.FindAll(IsCriticalFinding);
                if (snapshot.CpuTemperatureC >= 85) critical.Add("HWiNFO критично: CPU " + snapshot.CpuTemperatureC.Value.ToString("N0") + " °C");
                if (monitoringWindow.Count == 6)
                {
                    var cpuSamples = monitoringWindow.Where(item => item.CpuPercent.HasValue).Select(item => item.CpuPercent.Value).ToArray();
                    var memorySamples = monitoringWindow.Where(item => item.MemoryUsedPercent.HasValue).Select(item => item.MemoryUsedPercent.Value).ToArray();
                    if (cpuSamples.Count(value => value >= 98) >= 3)
                        critical.Add("HWiNFO критично: загрузка CPU держится на уровне 98% и выше");
                    if (memorySamples.Count(value => value >= 98) >= 3)
                        critical.Add("HWiNFO критично: загрузка ОЗУ держится на уровне 98% и выше");
                }
                Func<string,string> key=value=>System.Text.RegularExpressions.Regex.Replace(value,@"[-+]?\d+(?:[.,]\d+)?","#");
                var active=new HashSet<string>(critical.Select(key));
                foreach(var expired in reportedCritical.Where(item=>!active.Contains(item)).ToArray()) reportedCritical.Remove(expired);
                critical=critical.Where(value=>reportedCritical.Add(key(value))).ToList();
                if (critical.Count == 0) return;
                snapshot.Kind = "Постоянный мониторинг";
                snapshot.Findings = critical;
                ServerReportUploader.Queue(snapshot);
                if (ServerReportUploader.Upload() != 0) ServerReportUploader.Trigger();
            }

            private static bool IsCriticalFinding(string value)
            {
                return value != null && (value.StartsWith("SMART:", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("HWiNFO критично:",StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Мало свободного места", StringComparison.OrdinalIgnoreCase));
            }

            private static bool IsAdministrator(NamedPipeServerStream pipe)
            {
                var result = false;
                try
                {
                    pipe.RunAsClient(() =>
                    {
                        using (var identity = WindowsIdentity.GetCurrent())
                            result = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    });
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException) && !(ex is AccessViolationException))
                {
                    WriteError(ex);
                }
                return result;
            }

            private static string ClientSid(NamedPipeServerStream pipe)
            {
                var sid = "";
                try
                {
                    pipe.RunAsClient(() =>
                    {
                        using (var identity = WindowsIdentity.GetCurrent())
                            if (identity.User != null) sid = identity.User.Value;
                    });
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException) && !(ex is AccessViolationException))
                {
                    WriteError(ex);
                }
                return sid;
            }

            private static Dictionary<string, object> Response(bool success, string message, string runId = null)
            {
                return new Dictionary<string, object> { { "success", success }, { "accepted", success }, { "message", message }, { "runId", runId } };
            }

            private static string Value(Dictionary<string, object> values, string key)
            {
                object value;
                return values.TryGetValue(key, out value) ? Convert.ToString(value) : null;
            }

            private static string UtcNow() { return DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }

            private static void WriteStatus(Win7ServiceStatus status)
            {
                WriteAtomically(StatusPath, Serializer.Serialize(status));
            }

            private static void WriteError(Exception exception)
            {
                var key = exception.GetType().FullName + "|" + exception.Message;
                lock (ErrorGate)
                {
                    DateTime previous;
                    if (RecentErrors.TryGetValue(key, out previous) && DateTime.UtcNow - previous < TimeSpan.FromMinutes(5)) return;
                    RecentErrors[key] = DateTime.UtcNow;
                    if (RecentErrors.Count > 64)
                        foreach (var expired in RecentErrors.Where(item => DateTime.UtcNow - item.Value >= TimeSpan.FromMinutes(5)).Select(item => item.Key).ToArray())
                            RecentErrors.Remove(expired);
                }
                LegacyApplicationErrors.Record("Служба Win7", "Фоновая операция", exception);
                try
                {
                    Directory.CreateDirectory(ServiceRoot);
                    File.AppendAllText(Path.Combine(ServiceRoot, "service-error.log"), "[" + DateTime.UtcNow.ToString("o") + "] " + exception + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception) { }
            }

            private static void WriteAtomically(string path, string value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, value ?? "", new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    internal static class Win7MaintenanceServiceClient
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        internal static Task<Win7ServiceCallResult> PingAsync() { return SendAsync("ping", null); }
        internal static Task<Win7ServiceCallResult> StartAsync(bool full) { return SendAsync("start", full ? "full" : "quick"); }
        internal static Task<Win7ServiceCallResult> CleanupAsync() { return SendAsync("start", "cleanup"); }
        internal static Task<Win7ServiceCallResult> RepairAsync() { return SendAsync("start", "repair"); }
        internal static Task<Win7ServiceCallResult> StopAsync() { return SendAsync("stop", null); }
        internal static Task<Win7ServiceCallResult> LaunchSystemToolAsync(string target, int sessionId)
        {
            return SendAsync("launch", "system", target, null, sessionId);
        }
        internal static Task<Win7ServiceCallResult> LaunchUtilityAsync(string target, string argument, int sessionId)
        {
            return SendAsync("launch", "utility", target, argument, sessionId);
        }

        private static Task<Win7ServiceCallResult> SendAsync(string command, string operation, string target = null,
            string argument = null, int? sessionId = null)
        {
            return Task.Run(() =>
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", Win7MaintenanceServiceHost.PipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        pipe.Connect(2500);
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, true))
                        {
                            writer.WriteLine(Serializer.Serialize(new Dictionary<string, object> {
                                { "command", command }, { "operation", operation }, { "target", target },
                                { "argument", argument }, { "sessionId", sessionId }
                            }));
                            var line = reader.ReadLine();
                            if (string.IsNullOrWhiteSpace(line)) return Win7ServiceCallResult.Unavailable("Служба не вернула ответ.");
                            var response = Serializer.Deserialize<Dictionary<string, object>>(line);
                            if (response == null) return Win7ServiceCallResult.Unavailable("Служба вернула некорректный ответ.");
                            return new Win7ServiceCallResult(response.ContainsKey("success") && Convert.ToBoolean(response["success"]),
                                response.ContainsKey("accepted") && Convert.ToBoolean(response["accepted"]),
                                response.ContainsKey("message") ? Convert.ToString(response["message"]) : "",
                                response.ContainsKey("runId") ? Convert.ToString(response["runId"]) : null);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is System.TimeoutException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    return Win7ServiceCallResult.Unavailable(ex.Message);
                }
            });
        }
    }

    internal sealed class Win7ToolSpec
    {
        private Win7ToolSpec(string fileName, IList<string> arguments, string workingDirectory)
        {
            FileName = fileName;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
        }

        internal string FileName { get; private set; }
        internal IList<string> Arguments { get; private set; }
        internal string WorkingDirectory { get; private set; }

        internal static Win7ToolSpec FromSystem(string key)
        {
            var system = Environment.SystemDirectory;
            switch ((key ?? "").ToLowerInvariant())
            {
                case "compmgmt.msc": return Mmc(system, "compmgmt.msc");
                case "gpedit.msc": return Mmc(system, "gpedit.msc");
                case "appwiz.cpl": return new Win7ToolSpec(Path.Combine(system, "control.exe"), new List<string> { "appwiz.cpl" }, system);
                case "ncpa.cpl": return new Win7ToolSpec(Path.Combine(system, "control.exe"), new List<string> { "ncpa.cpl" }, system);
                case "control.exe": return new Win7ToolSpec(Path.Combine(system, "control.exe"), new List<string>(), system);
                case "msinfo32.exe": return new Win7ToolSpec(Path.Combine(system, "msinfo32.exe"), new List<string>(), system);
                default: throw new ArgumentException("Неизвестное системное приложение Windows 7.", "target");
            }
        }

        internal static Win7ToolSpec Utility(string key, string argument)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            string file;
            switch ((key ?? "").ToLowerInvariant())
            {
                case "crystaldiskinfo":
                    file = Path.Combine(root, "Tools", "CrystalDiskInfo", Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe");
                    break;
                case "crystaldiskmark":
                    file = Path.Combine(root, "Tools", "CrystalDiskMark9", Environment.Is64BitOperatingSystem ? "DiskMark64.exe" : "DiskMark32.exe");
                    break;
                case "hwinfo":
                    file = Path.Combine(root, "Tools", "HWiNFO", "HWiNFO32.exe");
                    break;
                case "treesize":
                    file = Path.Combine(root, "Tools", "TreeSize", "TreeSize.exe");
                    if (!string.IsNullOrWhiteSpace(argument) && !global::System.Text.RegularExpressions.Regex.IsMatch(argument, "^[A-Za-z]:\\\\$"))
                        throw new ArgumentException("TreeSize можно запустить только для локального диска.", "argument");
                    return Existing(file, string.IsNullOrWhiteSpace(argument) ? new List<string>() : new List<string> { argument });
                default: throw new ArgumentException("Утилита не разрешена службой.", "target");
            }
            return Existing(file, new List<string>());
        }

        private static Win7ToolSpec Mmc(string system, string name)
        {
            return new Win7ToolSpec(Path.Combine(system, "mmc.exe"), new List<string> { Path.Combine(system, name) }, system);
        }

        private static Win7ToolSpec Existing(string file, IList<string> arguments)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Утилита не найдена в установленном комплекте.", file);
            return new Win7ToolSpec(file, arguments, Path.GetDirectoryName(file));
        }
    }

    internal static class Win7InteractiveProcessLauncher
    {
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateNewConsole = 0x00000010;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenSessionId = 12;

        internal static int LaunchAsSystem(int sessionId, string executable, IList<string> arguments, string workingDirectory)
        {
            if (sessionId <= 0) throw new ArgumentOutOfRangeException("sessionId");
            if (!Path.IsPathRooted(executable) || !File.Exists(executable)) throw new FileNotFoundException("Утилита не найдена.", executable);
            if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
            var access = TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId;
            IntPtr source;
            if (!OpenProcessToken(GetCurrentProcess(), access, out source)) ThrowLastError("Не удалось получить токен службы.");
            try
            {
                IntPtr primary;
                if (!DuplicateTokenEx(source, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary)) ThrowLastError("Не удалось подготовить повышенный токен.");
                try
                {
                    var session = sessionId;
                    if (!SetTokenInformation(primary, TokenSessionId, ref session, (uint)Marshal.SizeOf(typeof(int)))) ThrowLastError("Не удалось выбрать пользовательский сеанс Windows.");
                    IntPtr environment;
                    if (!CreateEnvironmentBlock(out environment, primary, false)) ThrowLastError("Не удалось подготовить окружение процесса.");
                    try
                    {
                        var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = @"winsta0\default" };
                        var command = new StringBuilder(Quote(executable) + string.Concat(arguments.Select(x => " " + Quote(x))));
                        ProcessInformation information;
                        if (!CreateProcessAsUser(primary, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                            CreateUnicodeEnvironment | CreateNewConsole, environment, workingDirectory, ref startup, out information))
                            ThrowLastError("Windows не запустила повышенную утилиту.");
                        try { return checked((int)information.ProcessId); }
                        finally { CloseHandle(information.ThreadHandle); CloseHandle(information.ProcessHandle); }
                    }
                    finally { DestroyEnvironmentBlock(environment); }
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(source); }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (!value.Any(char.IsWhiteSpace) && value.IndexOf('"') < 0) return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        private static void ThrowLastError(string message) { throw new Win32Exception(Marshal.GetLastWin32Error(), message); }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr primaryToken);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref int tokenInformation, uint tokenInformationLength);
        [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            public int Size; public string Reserved; public string Desktop; public string Title; public int X; public int Y; public int XSize; public int YSize; public int XCountChars; public int FillAttribute; public int Flags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Pointer; public IntPtr StandardInput; public IntPtr StandardOutput; public IntPtr StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr ProcessHandle; public IntPtr ThreadHandle; public uint ProcessId; public uint ThreadId; }
    }
}
