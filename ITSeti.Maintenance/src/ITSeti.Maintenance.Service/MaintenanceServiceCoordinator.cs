using ITSeti.Maintenance.Core;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal sealed class MaintenanceServiceCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan liveSnapshotTimeout = TimeSpan.FromSeconds(8);
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
    private readonly ConcurrentDictionary<MaintenanceServiceOperation, Process> running = new();
    private readonly ConcurrentDictionary<MaintenanceServiceOperation, string> operationTriggers = new();
    private readonly ConcurrentDictionary<MaintenanceServiceOperation, int> requestingSessions = new();
    private readonly ConcurrentDictionary<Guid, byte> interactivePackageRuns = new();
    private readonly object operationStartGate = new();
    private readonly OperationQueue<MaintenanceServiceOperation> operations = new();
    private bool stopping;
    private readonly SemaphoreSlim temperatureLock = new(1, 1);
    private readonly TicketUpdateMonitor ticketUpdateMonitor;
    private readonly string backendRoot;
    private readonly string statusPath;
    private readonly string pipeName;
    private readonly bool monitorEnabled;
    private readonly MonitoringConfirmation monitoring = new();
    private readonly DiskLatencySampler diskLatencySampler = new();
    private readonly EngineerAuthorization engineerAuthorization = new();
    private Task<HwInfoSnapshot?>? hwInfoReadTask;
    private bool hwInfoReadTimedOut;
    private Task<DiagnosticSnapshot>? liveSnapshotTask;
    private DiagnosticSnapshot? lastLiveSnapshot;
    private bool liveSnapshotTimedOut;
    private bool temperatureProbeFailed;
    private DateTimeOffset nextLiveSnapshotErrorUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextTemperatureErrorUtc = DateTimeOffset.MinValue;
    private readonly object ticketPollGate = new();
    private readonly object reportUploadGate = new();
    private DateTimeOffset lastTemperatureProbe = DateTimeOffset.MinValue;
    private DateTimeOffset lastUpload = DateTimeOffset.MinValue;
    private DateTimeOffset nextTicketPoll = DateTimeOffset.MinValue;
    private bool ticketPollRunning;
    private bool ticketPollRequested;

    public MaintenanceServiceCoordinator(string? backendRootOverride = null, string? statusPathOverride = null,
        string? pipeNameOverride = null) : this(backendRootOverride, statusPathOverride, pipeNameOverride, true) { }

    internal MaintenanceServiceCoordinator(string? backendRootOverride, string? statusPathOverride,
        string? pipeNameOverride, bool monitorEnabled)
    {
        ticketUpdateMonitor = new TicketUpdateMonitor(onIncomingMessages: WtsTicketNotification.Show);
        backendRoot = backendRootOverride ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Backend"));
        statusPath = statusPathOverride ?? Path.Combine(DataRoot, "Service", "status.json");
        pipeName = pipeNameOverride ?? MaintenanceServiceProtocol.PipeName;
        this.monitorEnabled = monitorEnabled;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        ValidateInstallation();
        await WriteStatusAsync(null, "Служба запущена", cancellationToken);
        var pipe = RunPipeLoopAsync(cancellationToken);
        var monitor = monitorEnabled ? RunMonitorLoopAsync(cancellationToken) : Task.CompletedTask;
        try { await Task.WhenAll(pipe, monitor); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { StopChildren(); HwInfoReader.StopSession(); }
    }

    internal void ValidateInstallation()
    {
        foreach (var name in new[] { "InstalledCheck.ps1", "InstalledCleanup.ps1", "InstalledRepair.ps1",
                     "InstalledOrganizationSetup.ps1", "Upload-Reports.ps1", "ServerSoftware.ps1", "Set-WindowsAutomaticUpdates.ps1", "WindowsDefender.ps1" })
            if (!File.Exists(Path.Combine(backendRoot, name)))
                throw new FileNotFoundException("Не найден обязательный сценарий службы.", name);
    }

    private async Task RunPipeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleClientAsync(pipe, cancellationToken);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        MaintenanceServiceResponse response;
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await ReadBoundedLineAsync(reader, requestTimeout.Token);
            if (string.IsNullOrWhiteSpace(line))
                response = new(false, "Некорректный запрос.", GetVersion());
            else
            {
                var request = JsonSerializer.Deserialize<MaintenanceServiceRequest>(line, JsonOptions);
                var client = ServiceClientAuthorization.Identify(pipe);
                response = request is null ? new(false, "Некорректный запрос.", GetVersion()) : HandleRequest(request, client);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), requestTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (InvalidDataException ex)
        {
            var client = ServiceClientAuthorization.Identify(pipe);
            var details = $"{ex.Message} Клиентский процесс: PID={client.ProcessId}; " +
                $"сеанс={client.SessionId}; установленный клиент={client.IsInstalledClient}. " +
                "Содержимое запроса не записано, чтобы не сохранять пароль или ключи подключения.";
            ApplicationErrorStore.Record("Системная служба", "Отклонение запроса IPC",
                new InvalidDataException(details, ex),
                "Служба отклонила локальный IPC-запрос, превышающий допустимый размер.");
        }
        catch (IOException ex) when (IsExpectedPipeDisconnect(ex)) { }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            await WriteServiceErrorAsync(ex);
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= MaintenanceServiceProtocol.MaximumMessageCharacters)
                throw new InvalidDataException($"Размер запроса превышает {MaintenanceServiceProtocol.MaximumMessageCharacters} символов; получено не менее {result.Length + 1}. Содержимое не записывается в журнал.");
            result.Append(buffer[0]);
        }
    }

    private static bool IsExpectedPipeDisconnect(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 109 or 232 or 233 ||
            exception.Message.Contains("pipe is broken", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("pipe has been ended", StringComparison.OrdinalIgnoreCase);
    }

    private MaintenanceServiceResponse HandleRequest(MaintenanceServiceRequest request, ServiceClientIdentity client)
    {
        if (string.Equals(request.Command, "ping", StringComparison.OrdinalIgnoreCase))
            return new(true, "Служба обслуживания работает.", GetVersion());
        if (!client.IsInstalledClient)
            return new(false, "Запрос отклонён: требуется установленное приложение ИТ-Сети.", GetVersion());
        if (string.Equals(request.Command, "authenticate", StringComparison.OrdinalIgnoreCase))
        {
            var token = engineerAuthorization.Authenticate(request.Password, client);
            return token is null
                ? new(false, "Неверный пароль инженера или временная блокировка после нескольких попыток.", GetVersion())
                : new(true, "Режим инженера открыт.", GetVersion(), token);
        }
        if (string.Equals(request.Command, "refresh-ticket-updates", StringComparison.OrdinalIgnoreCase))
        {
            RequestTicketUpdatePoll();
            return new(true, "Синхронизация заявок запрошена.", GetVersion());
        }
        if (string.Equals(request.Command, "save-enrollment", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SaveEnrollment(request.Enrollment, Path.Combine(DataRoot, "server-device.json"));
                return new(true, "Привязка ПК сохранена.", GetVersion());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new(false, ex.Message, GetVersion());
            }
        }
        if (string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) && request.Operation is { } stopOperation)
        {
            var authorized = engineerAuthorization.IsAuthorized(request.AuthorizationToken, client);
            if (!authorized && !CanStopWithoutEngineerAuthorization(stopOperation))
                return new(false, "Для остановки этой операции нужен пароль инженера.", GetVersion());
            if (stopOperation == MaintenanceServiceOperation.QuickCheck)
                stopOperation = MaintenanceServiceOperation.FullCheck;
            if (IsDiagnosticOperation(stopOperation))
            {
                Func<MaintenanceServiceOperation, bool> canStop = authorized
                    ? IsDiagnosticOperation
                    : CanStopWithoutEngineerAuthorization;
                lock (operationStartGate)
                    foreach (var pending in operations.Pending.Where(operation => IsDiagnosticOperation(operation) && canStop(operation)))
                    {
                        operations.Cancel(pending);
                        operationTriggers.TryRemove(pending, out _);
                        requestingSessions.TryRemove(pending, out _);
                    }
                var stopped = false;
                foreach (var operation in running.Keys.Where(operation => IsDiagnosticOperation(operation) && canStop(operation)))
                    stopped |= StopOperation(operation);
                return new(true, stopped ? "Проверка остановлена." : "Проверка уже завершена; очередь очищена.", GetVersion());
            }
            return new(true, StopOperation(stopOperation) ? "Операция остановлена." : "Операция уже завершена.", GetVersion());
        }
        if (string.Equals(request.Command, "launch", StringComparison.OrdinalIgnoreCase) && request.Operation is
            MaintenanceServiceOperation.OpenSystemTool or MaintenanceServiceOperation.OpenUtility)
        {
            if (!engineerAuthorization.IsAuthorized(request.AuthorizationToken, client))
                return new(false, "Для запуска системной утилиты нужен пароль инженера.", GetVersion());
            if (request.SessionId != client.SessionId)
                return new(false, "Сеанс пользователя не совпадает с запрашивающим процессом.", GetVersion());
            try
            {
                var processId = LaunchInteractiveTool(request);
                return new(true, $"Утилита запущена службой (PID {processId}).", GetVersion());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or Win32Exception or ArgumentException)
            {
                ApplicationErrorStore.Record("Системная служба", "Запуск утилиты " + request.Target, ex);
                return new(false, ex.Message, GetVersion());
            }
        }
        if (string.Equals(request.Command, "install-package", StringComparison.OrdinalIgnoreCase) &&
            request.Operation == MaintenanceServiceOperation.OpenServerPackage)
        {
            if (!engineerAuthorization.IsAuthorized(request.AuthorizationToken, client))
                return new(false, "Для запуска установщика с системными правами нужен пароль инженера.", GetVersion());
            if (request.SessionId != client.SessionId)
                return new(false, "Сеанс пользователя не совпадает с запрашивающим процессом.", GetVersion());
            if (!Guid.TryParse(request.Target, out var packageId) || packageId == Guid.Empty ||
                !Guid.TryParseExact(request.Argument, "N", out var requestId) || requestId == Guid.Empty)
                return new(false, "Некорректный идентификатор пакета или запуска.", GetVersion());
            var resultPath = GetInteractivePackageResultPath(requestId);
            if (File.Exists(resultPath) || !interactivePackageRuns.TryAdd(requestId, 0))
                return new(false, "Этот запрос на запуск установщика уже обработан.", GetVersion());
            _ = PrepareAndLaunchServerPackageAsync(packageId, requestId, client.SessionId);
            return new(true, "Подготавливаем проверенный установщик…", GetVersion());
        }
        if (!string.Equals(request.Command, "start", StringComparison.OrdinalIgnoreCase) || request.Operation is null)
            return new(false, "Команда службы не поддерживается.", GetVersion());
        var normalizedOperation = request.Operation == MaintenanceServiceOperation.QuickCheck
            ? MaintenanceServiceOperation.FullCheck : request.Operation.Value;
        if (!ServiceClientAuthorization.OrdinaryOperation(normalizedOperation) &&
            !engineerAuthorization.IsAuthorized(request.AuthorizationToken, client))
            return new(false, "Для этой операции нужен пароль инженера.", GetVersion());
        try
        {
            if (IsDiagnosticOperation(normalizedOperation) && HasRunningDiagnostic())
            {
                if (normalizedOperation == MaintenanceServiceOperation.DailySmartCheck)
                    return new(true, "Диагностическая проверка уже выполняется.", GetVersion());
                if (running.Any(pair => pair.Key is MaintenanceServiceOperation.FullCheck or MaintenanceServiceOperation.ScheduledFullCheck && IsRunning(pair.Value)))
                    return new(true, running.Any(pair => pair.Key == MaintenanceServiceOperation.ScheduledFullCheck && IsRunning(pair.Value))
                        ? "Присоединяемся к текущей плановой полной проверке."
                        : "Присоединяемся к текущей полной проверке.", GetVersion());
                lock (operationStartGate)
                {
                    operationTriggers[normalizedOperation] = "Запуск пользователем из приложения";
                    requestingSessions[normalizedOperation] = client.SessionId;
                    operations.Enqueue(normalizedOperation, "diagnostics");
                }
                return new(true, "Диагностическая проверка уже выполняется; запрос поставлен в очередь.", GetVersion());
            }
            var started = StartOperation(normalizedOperation, "Запуск пользователем из приложения", client.SessionId);
            bool queueable;
            bool alreadyRunning;
            lock (operationStartGate)
            {
                queueable = operations.Pending.Contains(normalizedOperation);
                alreadyRunning = running.ContainsKey(normalizedOperation);
            }
            return new(started || queueable || alreadyRunning,
                started ? "Операция принята службой." : queueable ? "Операция уже выполняется; запрос оставлен в очереди." : "Операция уже выполняется.", GetVersion());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ApplicationErrorStore.Record("Системная служба", "Запуск " + normalizedOperation, ex);
            return new(false, ex.Message, GetVersion());
        }
    }

    internal static void SaveEnrollment(DeviceEnrollmentData? enrollment, string path)
    {
        if (enrollment is null || !Uri.TryCreate(enrollment.ServerUrl, UriKind.Absolute, out var server) ||
            server.Scheme != Uri.UriSchemeHttps || !server.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) ||
            server.Port != 443 || !string.IsNullOrEmpty(server.UserInfo) ||
            !Guid.TryParse(enrollment.DeviceId, out _) || string.IsNullOrWhiteSpace(enrollment.DeviceKey) ||
            enrollment.DeviceKey.Length is < 16 or > 512 ||
            enrollment.SupportKey?.Length > 512 || enrollment.CompanyId is <= 0 || enrollment.SiteId is <= 0)
            throw new InvalidDataException("Некорректные данные привязки ПК.");

        var operationPath = path;
        var step = "проверка существующей привязки";
        try
        {
            if (File.Exists(path))
            {
                DeviceEnrollmentData? existing;
                try { existing = JsonSerializer.Deserialize<DeviceEnrollmentData>(File.ReadAllText(path), JsonOptions); }
                catch (JsonException ex) { throw new InvalidDataException("Сохранённый ключ ПК повреждён.", ex); }
                if (existing is null || existing.DeviceId != enrollment.DeviceId || existing.DeviceKey != enrollment.DeviceKey)
                    throw new InvalidDataException("Ключ ПК изменился. Повторное подключение требует отдельного восстановления.");
            }
            step = "создание каталога для привязки";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            step = "сохранение ключа подключения";
            WriteTextAtomically(path, JsonSerializer.Serialize(enrollment, JsonOptions));
            if (string.IsNullOrWhiteSpace(enrollment.SupportKey)) return;
            operationPath = Path.Combine(Path.GetDirectoryName(path)!, "server-support.json");
            step = "сохранение ключа поддержки";
            WriteTextAtomically(operationPath, JsonSerializer.Serialize(new
            {
                serverUrl = enrollment.ServerUrl, deviceId = enrollment.DeviceId, supportKey = enrollment.SupportKey
            }, JsonOptions));
        }
        catch (UnauthorizedAccessException ex)
        {
            if (ex.Message.StartsWith("Нет доступа при ", StringComparison.Ordinal)) throw;
            throw CreateAccessDeniedException(operationPath, step, ex);
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var step = "создание временного файла";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            step = "атомарная замена целевого файла";
            File.Move(temporary, path, true);
        }
        catch (UnauthorizedAccessException ex) { throw CreateAccessDeniedException(path, step, ex); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static UnauthorizedAccessException CreateAccessDeniedException(string path, string step,
        UnauthorizedAccessException exception)
    {
        var directory = Path.GetDirectoryName(path)!;
        return new UnauthorizedAccessException(
            $"Нет доступа при {step}; путь={path}; причина={exception.Message}; HRESULT=0x{unchecked((uint)exception.HResult):X8}; " +
            $"Windows identity={GetCurrentWindowsIdentity()}; каталог существует={Directory.Exists(directory)}; " +
            $"целевой файл существует={File.Exists(path)}; права каталога: {DescribeFileSystemSecurity(directory)}; " +
            $"права целевого файла: {DescribeFileSystemSecurity(path)}.", exception);
    }

    private bool TryStartReportUpload()
    {
        lock (reportUploadGate)
        {
            var now = DateTimeOffset.UtcNow;
            var attemptPath = Path.Combine(DataRoot, "report-upload-last-attempt.txt");
            var persistedAttempt = DateTimeOffset.MinValue;
            if (File.Exists(attemptPath))
                DateTimeOffset.TryParse(File.ReadAllText(attemptPath), out persistedAttempt);
            if (now - lastUpload < TimeSpan.FromHours(1) || now - persistedAttempt < TimeSpan.FromHours(1) ||
                !File.Exists(Path.Combine(DataRoot, "server-device.json"))) return false;
            // Record attempts, including failed requests, so an unavailable server cannot
            // make the service retry every five seconds or immediately after a restart.
            if (!StartOperation(MaintenanceServiceOperation.UploadReports)) return false;
            lastUpload = now;
            var temporary = attemptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(DataRoot);
                File.WriteAllText(temporary, now.ToString("O"), Encoding.ASCII);
                if (File.Exists(attemptPath)) File.Replace(temporary, attemptPath, null);
                else File.Move(temporary, attemptPath);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
            return true;
        }
    }

    private bool StartOperation(MaintenanceServiceOperation operation, string? trigger = null,
        int? requestingSessionId = null)
    {
        lock (operationStartGate)
        {
            if (running.ContainsKey(operation)) return false;
            if (trigger is not null && IsDiagnosticOperation(operation))
                operationTriggers[operation] = trigger;
            if (requestingSessionId is { } sessionId && IsDiagnosticOperation(operation))
                requestingSessions[operation] = sessionId;
            return StartOperationLocked(operation);
        }
    }

    private bool StartOperationLocked(MaintenanceServiceOperation operation)
    {
        if (operation == MaintenanceServiceOperation.ProbeTemperature)
        {
            _ = RunTemperatureOperationAsync();
            return true;
        }
        // Even an exited worker owns its slot until the observer drains output and completes its state.
        if (running.ContainsKey(operation)) return false;
        if (stopping) return false;
        operations.Enqueue(operation, OperationGroup(operation), retry: operations.Snapshot.Any(item => item.Operation == operation && item.State == OperationState.Failed));
        if (!operations.TryStart(operation)) return false;
        Process? process = null;
        try
        {
            var (script, arguments) = ResolveOperation(operation);
            var trigger = operationTriggers.TryGetValue(operation, out var queuedTrigger) ? queuedTrigger :
                operation == MaintenanceServiceOperation.ScheduledFullCheck ? "Плановая проверка: запуск задачи Windows" :
                operation == MaintenanceServiceOperation.DailySmartCheck ? "Фоновая быстрая проверка службы" :
                "Запуск службой";
            int? requestingSessionId = requestingSessions.TryRemove(operation, out var sessionId) ? sessionId : null;
            process = CreatePowerShellProcess(script, arguments, trigger, requestingSessionId);
            operationTriggers.TryRemove(operation, out _);
            if (!process.Start()) throw new InvalidOperationException("Windows не запустила операцию службы.");
            running[operation] = process;
            _ = ObserveProcessAsync(operation, script, process);
            return true;
        }
        catch (Exception ex)
        {
            operations.Complete(operation, ex.Message);
            process?.Dispose();
            throw;
        }
    }

    private async Task StartPendingOperationsAsync()
    {
        var failures = new List<Exception>();
        lock (operationStartGate)
        {
            if (stopping) return;
            foreach (var next in operations.Pending)
            {
                try { StartOperationLocked(next); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
                { failures.Add(ex); }
            }
        }
        foreach (var failure in failures) await WriteServiceErrorAsync(failure);
    }

    private static string OperationGroup(MaintenanceServiceOperation operation) => IsWindowsMaintenanceOperation(operation)
        ? "windows-maintenance" : IsSerializedSoftwareOperation(operation) ? "software" : operation.ToString();

    private static bool IsWindowsMaintenanceOperation(MaintenanceServiceOperation operation) =>
        IsDiagnosticOperation(operation) || operation is MaintenanceServiceOperation.Cleanup or MaintenanceServiceOperation.Repair;

    private static bool IsSerializedSoftwareOperation(MaintenanceServiceOperation operation) => operation is
        MaintenanceServiceOperation.OrganizationSetup or MaintenanceServiceOperation.UploadReports or
        MaintenanceServiceOperation.RefreshSoftwareCatalog;

    private async Task RunTemperatureOperationAsync()
    {
        try
        {
            await RefreshTemperatureAsync(CancellationToken.None);
            lastTemperatureProbe = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await WriteServiceErrorAsync(ex);
        }
    }

    private bool StopOperation(MaintenanceServiceOperation operation)
    {
        lock (operationStartGate)
        {
            operations.Cancel(operation);
            if (!running.ContainsKey(operation))
            {
                operationTriggers.TryRemove(operation, out _);
                requestingSessions.TryRemove(operation, out _);
            }
        }
        if (!running.TryGetValue(operation, out var process) || process.HasExited) return false;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return false; }
        return true;
    }

    private (string Script, string[] Arguments) ResolveOperation(MaintenanceServiceOperation operation) => operation switch
    {
        MaintenanceServiceOperation.FullCheck => ("InstalledCheck.ps1", []),
        MaintenanceServiceOperation.QuickCheck => ("InstalledCheck.ps1", []),
        MaintenanceServiceOperation.DailySmartCheck => ("InstalledCheck.ps1", ["-SmartOnly"]),
        MaintenanceServiceOperation.ScheduledFullCheck => ("InstalledCheck.ps1", ["-StartRepair", "-UserApproved"]),
        MaintenanceServiceOperation.Cleanup => ("InstalledCleanup.ps1", []),
        MaintenanceServiceOperation.Repair => ("InstalledRepair.ps1", []),
        MaintenanceServiceOperation.OrganizationSetup => ("InstalledOrganizationSetup.ps1", []),
        MaintenanceServiceOperation.UploadReports => ("Upload-Reports.ps1", []),
        MaintenanceServiceOperation.RefreshSoftwareCatalog => ("ServerSoftware.ps1", ["-CatalogOnly"]),
        MaintenanceServiceOperation.DisableWindowsUpdates => ("Set-WindowsAutomaticUpdates.ps1", ["-Action", "Disable"]),
        MaintenanceServiceOperation.RestoreWindowsUpdates => ("Set-WindowsAutomaticUpdates.ps1", ["-Action", "Restore"]),
        MaintenanceServiceOperation.DisableWindowsDefender => ("WindowsDefender.ps1", ["-Action", "Disable", "-StatusPath", Path.Combine(DataRoot, "WindowsDefender", "status.json")]),
        MaintenanceServiceOperation.EnableWindowsDefender => ("WindowsDefender.ps1", ["-Action", "Enable", "-StatusPath", Path.Combine(DataRoot, "WindowsDefender", "status.json")]),
        MaintenanceServiceOperation.MinimizeWindowsDiagnosticData => ("Set-WindowsDiagnosticData.ps1", ["-Action", "Minimize"]),
        MaintenanceServiceOperation.RestoreWindowsDiagnosticData => ("Set-WindowsDiagnosticData.ps1", ["-Action", "Restore"]),
        MaintenanceServiceOperation.ProbeTemperature => throw new InvalidOperationException("Опрос температуры выполняется самой службой."),
        _ => throw new InvalidOperationException("Операция службы не поддерживается.")
    };

    private int LaunchInteractiveTool(MaintenanceServiceRequest request)
    {
        if (request.SessionId is not > 0)
            throw new InvalidOperationException("Не удалось определить пользовательский сеанс Windows.");
        if (string.IsNullOrWhiteSpace(request.Target))
            throw new ArgumentException("Не указана разрешённая утилита.", nameof(request));

        SystemToolLaunchSpec spec;
        if (request.Operation == MaintenanceServiceOperation.OpenSystemTool)
        {
            spec = SystemToolsLauncher.GetLaunchSpec(request.Target);
        }
        else
        {
            var toolsRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(backendRoot)!, "Tools"));
            spec = request.Target switch
            {
                "CrystalDiskInfo" => CreateToolSpec(
                    FullDiagnosticsRunner.ResolveInteractiveDiskTool(toolsRoot, diskInfo: true), request.Target),
                "CrystalDiskMark" => CreateToolSpec(
                    FullDiagnosticsRunner.ResolveInteractiveDiskTool(toolsRoot, diskInfo: false), request.Target),
                "HWiNFO" => CreateToolSpec(FindHwInfoExecutable(toolsRoot), request.Target),
                "TreeSize" => CreateTreeSizeSpec(toolsRoot, request.Argument),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Target), "Утилита не разрешена службой.")
            };
        }
        return ElevatedInteractiveProcessLauncher.LaunchAsSystem(request.SessionId.Value,
            spec.FileName, spec.Arguments, spec.WorkingDirectory);
    }

    private async Task PrepareAndLaunchServerPackageAsync(Guid packageId, Guid requestId, int sessionId)
    {
        var resultPath = GetInteractivePackageResultPath(requestId);
        try
        {
            var package = ServerSoftwareCatalog.Read().SingleOrDefault(item => item.Id == packageId)
                ?? throw new InvalidOperationException("Пакет отсутствует в актуальном каталоге сервера.");
            var extension = ValidateInteractivePackage(package);
            var packageDirectory = Path.GetFullPath(Path.Combine(DataRoot, "ServerPackages"));
            Directory.CreateDirectory(packageDirectory);
            var installerPath = Path.GetFullPath(Path.Combine(packageDirectory, packageId.ToString("N") + extension));
            if (!installerPath.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь установщика вышел за пределы каталога пакетов.");

            if (!await IsVerifiedPackageAsync(installerPath, package))
                await DownloadAndVerifyPackageAsync(package, installerPath);

            var executable = extension == ".msi" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe") : installerPath;
            if (!File.Exists(executable)) throw new FileNotFoundException("Не найден системный установщик Windows.", executable);
            var arguments = extension == ".msi" ? new[] { "/i", installerPath } : Array.Empty<string>();
            var processId = ElevatedInteractiveProcessLauncher.LaunchAsSystem(sessionId, executable, arguments,
                Path.GetDirectoryName(installerPath)!);
            WriteInteractivePackageResult(resultPath, "RUNNING:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Системная служба", "Проверенный интерактивный установщик", ex);
            WriteInteractivePackageResult(resultPath, "ERROR:" + ex.Message);
        }
        finally { interactivePackageRuns.TryRemove(requestId, out _); }
    }

    private static string ValidateInteractivePackage(ServerSoftwarePackage package)
    {
        var key = package.Key?.Trim() ?? string.Empty;
        var fileName = package.FileName?.Trim() ?? string.Empty;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (package.Id == Guid.Empty || key.Length is < 1 or > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9._-]+$") ||
            key.Equals("application", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 48 ||
            Path.GetFileName(fileName) != fileName || fileName.Length > 180 ||
            extension is not (".exe" or ".msi") ||
            package.Platform is not ("windows" or "any") || package.SizeBytes is <= 0 or > 1_073_741_824 ||
            !System.Text.RegularExpressions.Regex.IsMatch(package.Sha256 ?? string.Empty, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Метаданные установщика не прошли проверку.");
        return extension;
    }

    private async Task DownloadAndVerifyPackageAsync(ServerSoftwarePackage package, string destination)
    {
        var enrollmentPath = Path.Combine(DataRoot, "server-device.json");
        using var enrollment = JsonDocument.Parse(await File.ReadAllTextAsync(enrollmentPath, Encoding.UTF8));
        var values = enrollment.RootElement;
        var serverUrl = values.GetProperty("serverUrl").GetString();
        var deviceId = values.GetProperty("deviceId").GetString();
        var deviceKey = values.GetProperty("deviceKey").GetString();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var configured) || configured.Scheme != Uri.UriSchemeHttps ||
            !configured.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) || configured.Port is not (443))
            throw new InvalidDataException("Адрес сервера в привязке недействителен.");
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceKey))
            throw new InvalidDataException("В привязке устройства отсутствуют данные авторизации.");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri("https://it-seti.nylenz.ru/api/v1/packages/" + package.Id.ToString("D") + "/download"));
        request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-Device-Key", deviceKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != package.SizeBytes)
            throw new InvalidDataException("Размер загружаемого установщика не совпадает с каталогом.");

        var temporary = destination + ".pending";
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0)
            {
                received += count;
                if (received > package.SizeBytes) throw new InvalidDataException("Установщик больше объявленного размера.");
                hasher.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count));
            }
            await output.FlushAsync();
            if (received != package.SizeBytes) throw new InvalidDataException("Загрузка установщика оборвалась.");
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (!actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("SHA-256 установщика не совпадает с серверным каталогом.");
            File.Move(temporary, destination, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static async Task<bool> IsVerifiedPackageAsync(string path, ServerSoftwarePackage package)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != package.SizeBytes) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            return hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string GetInteractivePackageResultPath(Guid requestId) => Path.Combine(DataRoot,
        "OrganizationSetupRuns", requestId.ToString("N"), "result.txt");

    private static void WriteInteractivePackageResult(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending";
        File.WriteAllText(temporary, value, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static SystemToolLaunchSpec CreateToolSpec(string? executable, string name)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new FileNotFoundException($"{name} не найден в установленном комплекте.");
        return new(executable, [], Path.GetDirectoryName(executable)!);
    }

    private static string? FindHwInfoExecutable(string toolsRoot)
    {
        foreach (var name in new[] { "HWiNFO32.exe", "HWiNFO64.exe" })
        {
            var path = Path.Combine(toolsRoot, "HWiNFO", name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static SystemToolLaunchSpec CreateTreeSizeSpec(string toolsRoot, string? volume)
    {
        if (string.IsNullOrWhiteSpace(volume) || !System.Text.RegularExpressions.Regex.IsMatch(volume, @"^[A-Za-z]:\\$"))
            throw new ArgumentException("TreeSize можно запустить только для локального диска.", nameof(volume));
        var executable = Path.Combine(toolsRoot, "TreeSize Free", "TreeSizeFree.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("TreeSize Free не найден в установленном комплекте.");
        return new(executable, [volume], Path.GetDirectoryName(executable)!);
    }

    private Process CreatePowerShellProcess(string scriptName, IReadOnlyList<string> arguments,
        string? checkTrigger = null, int? requestingSessionId = null)
    {
        var script = Path.GetFullPath(Path.Combine(backendRoot, scriptName));
        if (!script.StartsWith(backendRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
            throw new IOException("Сценарий операции службы отсутствует: " + scriptName);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(backendRoot)!
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            info.ArgumentList.Add(value);
        foreach (var value in arguments) info.ArgumentList.Add(value);
        if (scriptName.Equals("InstalledCheck.ps1", StringComparison.OrdinalIgnoreCase))
        {
            info.Environment["ITSETI_CHECK_TRIGGER"] = checkTrigger ?? "Запуск службой";
            if (requestingSessionId is { } sessionId)
                info.Environment["ITSETI_REQUESTED_SESSION_ID"] = sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private async Task ObserveProcessAsync(MaintenanceServiceOperation operation, string script, Process process)
    {
        string? failure = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var outputTask=ReadBoundedOutputAsync(process.StandardOutput);
            var errorTask=ReadBoundedOutputAsync(process.StandardError);
            await process.WaitForExitAsync();
            var output=await outputTask;
            var error=await errorTask;
            if (process.ExitCode != 0)
            {
                var exitCode = process.ExitCode;
                var exit = DescribeProcessExit(exitCode);
                var summary = $"Фоновая операция {operation} аварийно завершилась: {exit}.";
                var details = $"Операция: {operation}; сценарий: {Path.GetFullPath(script)}; " +
                    $"исполняемый файл: {process.StartInfo.FileName}; длительность: {elapsed.Elapsed}; завершение: {exit}." +
                    Environment.NewLine + "STDERR (последние строки):" + Environment.NewLine + LimitToTail(error, 5000) +
                    Environment.NewLine + "STDOUT (последние строки):" + Environment.NewLine + LimitToTail(output, 5000);
                ApplicationErrorStore.Record("Системная служба", $"Фоновая операция: {operation}",
                    new InvalidOperationException(details), summary);
                failure = BuildOperationFailureStatus(summary, error, output);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            failure = ex.Message;
            await WriteServiceErrorAsync(ex);
        }
        finally
        {
            lock (operationStartGate)
            {
                running.TryRemove(new KeyValuePair<MaintenanceServiceOperation, Process>(operation, process));
                operations.Complete(operation, failure);
            }
            process.Dispose();
            await StartPendingOperationsAsync();
            if (operation == MaintenanceServiceOperation.OrganizationSetup) RequestTicketUpdatePoll();
            if (IsDiagnosticOperation(operation) || operation == MaintenanceServiceOperation.Cleanup)
            {
                try { TryStartReportUpload(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { await WriteServiceErrorAsync(ex); }
            }
            if (failure is null && HasPendingRequests(operation))
            {
                try { StartOperation(operation); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { await WriteServiceErrorAsync(ex); }
            }
        }
    }

    private bool HasRunningDiagnostic() => running.Any(pair => IsDiagnosticOperation(pair.Key) && IsRunning(pair.Value));
    private static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader)
    {
        const int maximumCharacters = 65536;
        var output=new StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer))>0)
        {
            var remove = output.Length + count - maximumCharacters;
            if (remove > 0) output.Remove(0, remove);
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static string LimitToTail(string value, int maximumCharacters) => value.Length <= maximumCharacters
        ? value : "[…начало вывода сокращено…]" + value[^maximumCharacters..];

    private static string DescribeProcessExit(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        return unsigned == 0xC0000005
            ? $"STATUS_ACCESS_VIOLATION (код {exitCode}, 0x{unsigned:X8})"
            : $"код {exitCode} (0x{unsigned:X8})";
    }

    private static string BuildOperationFailureStatus(string summary, string error, string output)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
        detail = detail.ReplaceLineEndings(" ").Trim();
        if (detail.Length > 1400) detail = "…" + detail[^1400..];
        return string.IsNullOrWhiteSpace(detail) ? summary : summary + " " + detail;
    }

    private static bool IsDiagnosticOperation(MaintenanceServiceOperation operation) => operation is
        MaintenanceServiceOperation.FullCheck or MaintenanceServiceOperation.QuickCheck or
        MaintenanceServiceOperation.DailySmartCheck or MaintenanceServiceOperation.ScheduledFullCheck;

    private static bool CanStopWithoutEngineerAuthorization(MaintenanceServiceOperation operation) =>
        ServiceClientAuthorization.OrdinaryOperation(operation) || operation == MaintenanceServiceOperation.ScheduledFullCheck;

    private static bool HasPendingRequests(MaintenanceServiceOperation operation)
    {
        try
        {
            var request = operation switch
            {
                MaintenanceServiceOperation.Cleanup => (Folder: "CleanupRequests", Pattern: "*.request"),
                MaintenanceServiceOperation.OrganizationSetup => (Folder: "OrganizationSetupRequests", Pattern: "*.json"),
                _ => default
            };
            if (request.Folder is null) return false;
            var queue = Path.Combine(DataRoot, request.Folder);
            return Directory.Exists(queue) && Directory.EnumerateFiles(queue, request.Pattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStarted = Stopwatch.StartNew();
            try
            {
                if (DateTimeOffset.UtcNow - lastTemperatureProbe >= TimeSpan.FromSeconds(5))
                {
                    lastTemperatureProbe = DateTimeOffset.UtcNow;
                    try
                    {
                        await RefreshTemperatureAsync(cancellationToken);
                        temperatureProbeFailed = false;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        temperatureProbeFailed = true;
                        if (DateTimeOffset.UtcNow >= nextTemperatureErrorUtc)
                        {
                            nextTemperatureErrorUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                            await WriteServiceErrorAsync(ex);
                        }
                    }
                }
                var snapshot = await ReadLiveSnapshotWithTimeoutAsync(cancellationToken);
                IReadOnlyList<CriticalMonitoringIssue> critical = snapshot is null || liveSnapshotTimedOut
                    ? monitoring.Observe(Array.Empty<CriticalMonitoringIssue>(), DateTimeOffset.UtcNow)
                    : EvaluateMonitoring(snapshot);
                var state = liveSnapshotTimedOut
                    ? "Системный опрос задерживается; отображаются последние данные"
                    : temperatureProbeFailed
                        ? "Ошибка чтения датчиков; системный мониторинг продолжен"
                        : snapshot is null
                            ? "Ожидается первый снимок мониторинга"
                            : critical.Any(issue => issue.Severity == "Critical") ? "Обнаружено подтверждённое критическое состояние"
                            : critical.Count > 0 ? "Есть подтверждённое предупреждение датчиков" : "Мониторинг работает";
                await WriteStatusAsync(snapshot, state, cancellationToken, critical);
                if (snapshot is not null && critical.Count > 0)
                    await QueueMonitoringReportAsync(snapshot, critical, cancellationToken);
                TryStartReportUpload();
                if (DateTimeOffset.UtcNow >= nextTicketPoll) StartTicketUpdatePoll(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                await WriteServiceErrorAsync(ex);
            }
            catch (Exception ex)
            {
                await WriteServiceErrorAsync(ex);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50,5000-loopStarted.ElapsedMilliseconds)), cancellationToken);
        }
    }

    private async Task PollTicketUpdatesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (ticketPollGate) ticketPollRequested = false;
            try
            {
                var delay = await ticketUpdateMonitor.PollAsync(cancellationToken);
                nextTicketPoll = delay == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : DateTimeOffset.UtcNow + delay;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or
                KeyNotFoundException or FormatException or OverflowException)
            {
                ApplicationErrorStore.Record("Заявки", "Фоновое получение новых сообщений", ex);
                nextTicketPoll = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            lock (ticketPollGate)
            {
                if (ticketPollRequested && !cancellationToken.IsCancellationRequested) continue;
                ticketPollRunning = false;
                return;
            }
        }
    }

    private void RequestTicketUpdatePoll()
    {
        lock (ticketPollGate)
        {
            nextTicketPoll = DateTimeOffset.MaxValue;
            if (ticketPollRunning)
            {
                ticketPollRequested = true;
                return;
            }
            ticketPollRunning = true;
        }
        _ = PollTicketUpdatesAsync(CancellationToken.None);
    }

    private void StartTicketUpdatePoll(CancellationToken cancellationToken)
    {
        lock (ticketPollGate)
        {
            if (ticketPollRunning) return;
            ticketPollRunning = true;
        }
        _ = PollTicketUpdatesAsync(cancellationToken);
    }

    private IReadOnlyList<CriticalMonitoringIssue> EvaluateMonitoring(DiagnosticSnapshot snapshot) =>
        monitoring.Observe(CriticalMonitoringEvaluator.Evaluate(snapshot), snapshot.StartedAt);

    private Task QueueMonitoringReportAsync(ITSeti.Maintenance.Core.DiagnosticSnapshot snapshot,
        IReadOnlyList<CriticalMonitoringIssue> issues, CancellationToken cancellationToken)
    {
        var queue = Path.Combine(DataRoot, "MonitoringQueue");
        var report = new
        {
            snapshot.Id,
            snapshot.StartedAt,
            snapshot.ComputerName,
            snapshot.CpuPercent,
            snapshot.TotalMemoryBytes,
            snapshot.AvailableMemoryBytes,
            snapshot.Disks,
            snapshot.WindowsEdition,
            snapshot.WindowsRelease,
            snapshot.WindowsBuild,
            snapshot.CpuTemperatureC,
            snapshot.CpuTemperatureStatus,
            HWiNFO = snapshot.HwInfo is { } hwInfo ? new
            {
                hwInfo.ToolVersion,
                hwInfo.CapturedAtUtc,
                CpuTemperatureC = hwInfo.CpuTemperatureC,
                VrmTemperatureC = hwInfo.VrmTemperatureC,
                PchTemperatureC = hwInfo.PchTemperatureC,
                Rail12V = hwInfo.Rail12V,
                Rail5V = hwInfo.Rail5V,
                Rail3V = hwInfo.Rail3V,
                AlertingSensors = hwInfo.AlertingSensors
            } : null,
            snapshot.ActiveUptimeHours,
            snapshot.DiskReadLatencyMs,
            snapshot.DiskWriteLatencyMs,
            KindLabel = "Постоянный мониторинг",
            Trigger = "Служба непрерывно контролирует свежие показания датчиков и нагрузки",
            DiagnosticIssues = issues.Select(issue => new
            {
                issue.Title,
                issue.Detail,
                issue.Severity
            }).ToArray()
        };
        var path = Path.Combine(queue, snapshot.Id.ToString("N") + ".json");
        return WriteJsonAtomicallyAsync(path, report, cancellationToken);
    }

    private async Task RefreshTemperatureAsync(CancellationToken cancellationToken)
    {
        if (!await temperatureLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (HwInfoReader.SessionRestartDue)
                HwInfoReader.ResetSession();
            HwInfoSnapshot? hwInfo;
            try { hwInfo = await ReadHwInfoWithTimeoutAsync(cancellationToken); }
            catch (TimeoutException ex)
            {
                hwInfo = null;
                if (!hwInfoReadTimedOut)
                {
                    hwInfoReadTimedOut = true;
                    await WriteServiceErrorAsync(ex);
                }
            }
            var reading = hwInfo?.CpuTemperatureC is { } hwTemperature
                ? new CpuTemperatureReading(hwTemperature, $"HWiNFO · {hwInfo.Source}", hwInfo.CapturedAtUtc)
                : new CpuTemperatureReading(null,
                    "HWiNFO не вернул допустимый датчик CPU; запасной датчик не используется.",
                    DateTimeOffset.UtcNow);
            await WriteJsonAtomicallyAsync(CpuTemperatureCache.InstalledPath, reading, cancellationToken);
        }
        finally { temperatureLock.Release(); }
    }

    private async Task<DiagnosticSnapshot?> ReadLiveSnapshotWithTimeoutAsync(CancellationToken cancellationToken)
    {
        liveSnapshotTask ??= new WindowsDiagnosticsRunner().RunLiveAsync(diskLatencySampler, CancellationToken.None);
        var read = liveSnapshotTask;
        if (!read.IsCompleted)
        {
            if (liveSnapshotTimedOut) return lastLiveSnapshot;
            var delay = Task.Delay(liveSnapshotTimeout, cancellationToken);
            if (await Task.WhenAny(read, delay) != read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                liveSnapshotTimedOut = true;
                if (DateTimeOffset.UtcNow >= nextLiveSnapshotErrorUtc)
                {
                    nextLiveSnapshotErrorUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                    await WriteServiceErrorAsync(new TimeoutException(
                        $"Системный опрос не завершился за {liveSnapshotTimeout.TotalSeconds:0} секунд; повторный опрос не запускается до завершения текущего."));
                }
                return lastLiveSnapshot;
            }
        }

        try
        {
            lastLiveSnapshot = await read;
            liveSnapshotTask = null;
            liveSnapshotTimedOut = false;
            return lastLiveSnapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            liveSnapshotTask = null;
            liveSnapshotTimedOut = false;
            if (DateTimeOffset.UtcNow >= nextLiveSnapshotErrorUtc)
            {
                nextLiveSnapshotErrorUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                await WriteServiceErrorAsync(ex);
            }
            return lastLiveSnapshot;
        }
    }

    private async Task<HwInfoSnapshot?> ReadHwInfoWithTimeoutAsync(CancellationToken cancellationToken)
    {
        if (hwInfoReadTask is { IsCompleted: false }) return null;
        if (hwInfoReadTask is { IsCompleted: true } completed)
        {
            hwInfoReadTask = null;
            hwInfoReadTimedOut = false;
            return await completed;
        }

        var read = Task.Run(() => HwInfoReader.ReadAsync(CancellationToken.None));
        hwInfoReadTask = read;
        var delay = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        if (await Task.WhenAny(read, delay) != read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("HWiNFO не ответил за 10 секунд; цикл мониторинга продолжен, повторный запуск чтения не создаётся, пока текущий вызов не завершится.");
        }

        try
        {
            var result = await read;
            hwInfoReadTask = null;
            hwInfoReadTimedOut = false;
            return result;
        }
        catch
        {
            hwInfoReadTask = null;
            hwInfoReadTimedOut = false;
            throw;
        }
    }

    private Task WriteStatusAsync(ITSeti.Maintenance.Core.DiagnosticSnapshot? snapshot, string state,
        CancellationToken cancellationToken, IReadOnlyList<CriticalMonitoringIssue>? critical = null) => WriteJsonAtomicallyAsync(statusPath, new
        {
            Version = GetVersion(),
            State = state,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            MonitoringDataCapturedAtUtc = snapshot?.StartedAt,
            MonitoringDataAgeSeconds = snapshot is null ? null : (double?)Math.Max(0, (DateTimeOffset.UtcNow - snapshot.StartedAt).TotalSeconds),
            MonitoringDataStale = liveSnapshotTimedOut || snapshot is null,
            TemperatureProbeFailed = temperatureProbeFailed,
            ComputerName = snapshot?.ComputerName,
            CpuPercent = snapshot?.CpuPercent is { } cpu ? (double?)Math.Round(cpu) : null,
            MemoryUsedPercent = snapshot is null ? null : (double?)Math.Round(snapshot.MemoryUsedPercent),
            TotalMemoryBytes = snapshot?.TotalMemoryBytes,
            AvailableMemoryBytes = snapshot?.AvailableMemoryBytes,
            CpuTemperatureC = snapshot?.CpuTemperatureC is { } temperature ? (double?)Math.Round(temperature) : null,
            CpuTemperatureStatus = snapshot?.CpuTemperatureStatus,
            UptimeHours = snapshot?.ActiveUptimeHours is { } uptime ? (double?)Math.Round(uptime, 1) : null,
            DiskReadLatencyMs = snapshot?.DiskReadLatencyMs is { } readLatency ? (double?)Math.Round(readLatency, 1) : null,
            DiskWriteLatencyMs = snapshot?.DiskWriteLatencyMs is { } writeLatency ? (double?)Math.Round(writeLatency, 1) : null,
            HwInfo = snapshot?.HwInfo,
            WindowsEdition = snapshot?.WindowsEdition,
            WindowsRelease = snapshot?.WindowsRelease,
            WindowsBuild = snapshot?.WindowsBuild,
            CriticalIssues = critical?.Select(issue => new { issue.Identity, issue.Title, issue.Detail }).ToArray() ?? [],
            NetworkAdapters = snapshot?.NetworkAdapters,
            FixedDisks = snapshot?.Disks.Select(disk => new
            {
                disk.Name,
                FreeBytes = disk.FreeBytes,
                TotalBytes = disk.TotalBytes
            }).ToArray(),
            Operations = operations.Snapshot,
            RunningOperations = running.Keys.OrderBy(value => value).ToArray()
        }, cancellationToken);

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var step = "создание каталога";
        try
        {
            Directory.CreateDirectory(directory);
            step = "создание временного файла";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(false), cancellationToken);
            step = "атомарная замена целевого файла";
            File.Move(temporary, path, true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateAccessDeniedException(path, step, ex);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string GetCurrentWindowsIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"{identity.Name} ({identity.User?.Value ?? "SID не определён"})";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return "не определена: " + ex.Message; }
    }

    private static string DescribeFileSystemSecurity(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : File.Exists(path) ? new FileInfo(path).GetAccessControl() : throw new FileNotFoundException("Объект отсутствует.", path);
            var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? "владелец не определён";
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Take(16)
                .Select(rule => $"{(rule.AccessControlType == AccessControlType.Allow ? "ALLOW" : "DENY")}:{((SecurityIdentifier)rule.IdentityReference).Value}:{rule.FileSystemRights}:{(rule.IsInherited ? "inherited" : "explicit")}");
            return $"owner={owner}; ACE=[{string.Join(";", rules)}]";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { return "не удалось прочитать: " + ex.GetType().Name + ": " + ex.Message; }
    }

    private static Task WriteServiceErrorAsync(Exception exception)
    {
        ApplicationErrorStore.Record("Системная служба", "Фоновая операция", exception);
        return Task.CompletedTask;
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.4.7";

    private void StopChildren()
    {
        lock (operationStartGate)
        {
            stopping = true;
            foreach (var item in operations.Snapshot)
            {
                operations.Cancel(item.Operation);
                operationTriggers.TryRemove(item.Operation, out _);
                requestingSessions.TryRemove(item.Operation, out _);
            }
        }
        foreach (var process in running.Values)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        running.Clear();
    }

    public void Dispose()
    {
        StopChildren();
        ticketUpdateMonitor.Dispose();
        temperatureLock.Dispose();
        diskLatencySampler.Dispose();
    }
}
