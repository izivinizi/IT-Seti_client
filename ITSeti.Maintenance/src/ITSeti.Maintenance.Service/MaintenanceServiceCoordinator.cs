using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal sealed class MaintenanceServiceCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
    private readonly ConcurrentDictionary<MaintenanceServiceOperation, Process> running = new();
    private readonly SemaphoreSlim temperatureLock = new(1, 1);
    private readonly string backendRoot;
    private readonly string statusPath;
    private readonly string pipeName;
    private readonly Dictionary<string, int> monitoringStreaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reportedMonitoringIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly object diagnosticGate = new();
    private MaintenanceServiceOperation? queuedDiagnostic;
    private DateTimeOffset lastTemperatureProbe = DateTimeOffset.MinValue;
    private DateTimeOffset lastUpload = DateTimeOffset.MinValue;
    private DateTimeOffset lastCatalogRefresh = DateTimeOffset.MinValue;

    public MaintenanceServiceCoordinator(string? backendRootOverride = null, string? statusPathOverride = null,
        string? pipeNameOverride = null)
    {
        backendRoot = backendRootOverride ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Backend"));
        statusPath = statusPathOverride ?? Path.Combine(DataRoot, "Service", "status.json");
        pipeName = pipeNameOverride ?? MaintenanceServiceProtocol.PipeName;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        ValidateInstallation();
        await WriteStatusAsync(null, "Служба запущена", cancellationToken);
        var pipe = RunPipeLoopAsync(cancellationToken);
        var monitor = RunMonitorLoopAsync(cancellationToken);
        try { await Task.WhenAll(pipe, monitor); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { StopChildren(); }
    }

    internal void ValidateInstallation()
    {
        foreach (var name in new[] { "InstalledCheck.ps1", "InstalledCleanup.ps1", "InstalledRepair.ps1",
                     "InstalledOrganizationSetup.ps1", "Upload-Reports.ps1", "ServerSoftware.ps1", "Set-WindowsAutomaticUpdates.ps1" })
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
                var clientIsAdministrator = IsAdministrativeClient(pipe);
                response = request is null ? new(false, "Некорректный запрос.", GetVersion()) : HandleRequest(request, clientIsAdministrator);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), requestTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (InvalidDataException) { }
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
                throw new InvalidDataException("Запрос к службе превышает допустимый размер.");
            result.Append(buffer[0]);
        }
    }

    private static bool IsAdministrativeClient(NamedPipeServerStream pipe)
    {
        var result = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            result = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        return result;
    }

    private MaintenanceServiceResponse HandleRequest(MaintenanceServiceRequest request, bool clientIsAdministrator)
    {
        if (string.Equals(request.Command, "ping", StringComparison.OrdinalIgnoreCase))
            return new(true, "Служба обслуживания работает.", GetVersion());
        if (string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) && request.Operation is { } stopOperation)
            return new(true, StopOperation(stopOperation) ? "Операция остановлена." : "Операция уже завершена.", GetVersion());
        if (string.Equals(request.Command, "launch", StringComparison.OrdinalIgnoreCase) && request.Operation is
            MaintenanceServiceOperation.OpenSystemTool or MaintenanceServiceOperation.OpenUtility)
        {
            if (!clientIsAdministrator && IsAdministratorOnlyLaunch(request))
                return new(false, "Эта операция доступна только из инженерного меню администратора.", GetVersion());
            try
            {
                var processId = LaunchInteractiveTool(request);
                return new(true, $"Утилита запущена службой (PID {processId}).", GetVersion());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or Win32Exception or ArgumentException)
            {
                return new(false, ex.Message, GetVersion());
            }
        }
        if (!string.Equals(request.Command, "start", StringComparison.OrdinalIgnoreCase) || request.Operation is null)
            return new(false, "Команда службы не поддерживается.", GetVersion());
        try
        {
            if (IsDiagnosticOperation(request.Operation.Value) && HasRunningDiagnostic())
            {
                if (request.Operation.Value == MaintenanceServiceOperation.DailySmartCheck)
                    return new(true, "Диагностическая проверка уже выполняется.", GetVersion());
                lock (diagnosticGate) queuedDiagnostic = request.Operation.Value;
                return new(true, "Диагностическая проверка уже выполняется; запрос поставлен в очередь.", GetVersion());
            }
            var started = StartOperation(request.Operation.Value);
            var queueable = request.Operation.Value is MaintenanceServiceOperation.Cleanup
                or MaintenanceServiceOperation.OrganizationSetup
                or MaintenanceServiceOperation.UploadReports
                or MaintenanceServiceOperation.RefreshSoftwareCatalog;
            return new(started || queueable, started ? "Операция принята службой." : "Операция уже выполняется; запрос оставлен в очереди.", GetVersion());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(false, ex.Message, GetVersion());
        }
    }

    private static bool IsAdministratorOnlyLaunch(MaintenanceServiceRequest request) =>
        request.Operation == MaintenanceServiceOperation.OpenUtility &&
        string.Equals(request.Target, "CrystalDiskMark", StringComparison.OrdinalIgnoreCase) ||
        request.Operation == MaintenanceServiceOperation.OpenSystemTool &&
        string.Equals(request.Target, "terminal", StringComparison.OrdinalIgnoreCase);

    private bool StartOperation(MaintenanceServiceOperation operation)
    {
        if (operation == MaintenanceServiceOperation.ProbeTemperature)
        {
            _ = RunTemperatureOperationAsync();
            return true;
        }
        if (running.TryGetValue(operation, out var existing))
        {
            try { if (!existing.HasExited) return false; }
            catch (InvalidOperationException) { }
            running.TryRemove(new KeyValuePair<MaintenanceServiceOperation, Process>(operation, existing));
        }
        var (script, arguments) = ResolveOperation(operation);
        var process = CreatePowerShellProcess(script, arguments);
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила операцию службы.");
        if (!running.TryAdd(operation, process))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            return false;
        }
        _ = ObserveProcessAsync(operation, process);
        return true;
    }

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
        if (!running.TryGetValue(operation, out var process) || process.HasExited) return false;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return false; }
        return true;
    }

    private (string Script, string[] Arguments) ResolveOperation(MaintenanceServiceOperation operation) => operation switch
    {
        MaintenanceServiceOperation.FullCheck => ("InstalledCheck.ps1", []),
        MaintenanceServiceOperation.QuickCheck => ("InstalledCheck.ps1", ["-Quick"]),
        MaintenanceServiceOperation.DailySmartCheck => ("InstalledCheck.ps1", ["-SmartOnly"]),
        MaintenanceServiceOperation.ScheduledFullCheck => ("InstalledCheck.ps1", ["-StartRepair"]),
        MaintenanceServiceOperation.Cleanup => ("InstalledCleanup.ps1", []),
        MaintenanceServiceOperation.Repair => ("InstalledRepair.ps1", []),
        MaintenanceServiceOperation.OrganizationSetup => ("InstalledOrganizationSetup.ps1", []),
        MaintenanceServiceOperation.UploadReports => ("Upload-Reports.ps1", []),
        MaintenanceServiceOperation.RefreshSoftwareCatalog => ("ServerSoftware.ps1", ["-CatalogOnly"]),
        MaintenanceServiceOperation.DisableWindowsUpdates => ("Set-WindowsAutomaticUpdates.ps1", ["-Action", "Disable"]),
        MaintenanceServiceOperation.RestoreWindowsUpdates => ("Set-WindowsAutomaticUpdates.ps1", ["-Action", "Restore"]),
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
                "TreeSize" => CreateTreeSizeSpec(toolsRoot, request.Argument),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Target), "Утилита не разрешена службой.")
            };
        }
        return ElevatedInteractiveProcessLauncher.LaunchAsSystem(request.SessionId.Value,
            spec.FileName, spec.Arguments, spec.WorkingDirectory);
    }

    private static SystemToolLaunchSpec CreateToolSpec(string? executable, string name)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new FileNotFoundException($"{name} не найден в установленном комплекте.");
        return new(executable, [], Path.GetDirectoryName(executable)!);
    }

    private static SystemToolLaunchSpec CreateTreeSizeSpec(string toolsRoot, string? volume)
    {
        if (string.IsNullOrWhiteSpace(volume) || !System.Text.RegularExpressions.Regex.IsMatch(volume, @"^[A-Za-z]:\\$"))
            throw new ArgumentException("TreeSize можно запустить только для локального диска.", nameof(volume));
        var executable = Path.Combine(toolsRoot, "TreeSize Free", "TreeSizeFree.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("TreeSize Free не найден в установленном комплекте.");
        return new(executable, [volume], Path.GetDirectoryName(executable)!);
    }

    private Process CreatePowerShellProcess(string scriptName, IReadOnlyList<string> arguments)
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
            WorkingDirectory = Path.GetDirectoryName(backendRoot)!
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            info.ArgumentList.Add(value);
        foreach (var value in arguments) info.ArgumentList.Add(value);
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private async Task ObserveProcessAsync(MaintenanceServiceOperation operation, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                await WriteServiceErrorAsync(new InvalidOperationException($"{operation}: код завершения {process.ExitCode}."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await WriteServiceErrorAsync(ex);
        }
        finally
        {
            running.TryRemove(new KeyValuePair<MaintenanceServiceOperation, Process>(operation, process));
            process.Dispose();
            if (IsDiagnosticOperation(operation))
            {
                MaintenanceServiceOperation? next = null;
                lock (diagnosticGate)
                {
                    if (!HasRunningDiagnostic() && queuedDiagnostic is { } queued)
                    {
                        next = queued;
                        queuedDiagnostic = null;
                    }
                }
                if (next is { } nextOperation)
                {
                    try { StartOperation(nextOperation); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    { await WriteServiceErrorAsync(ex); }
                }
            }
            if (HasPendingRequests(operation))
            {
                try { StartOperation(operation); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { await WriteServiceErrorAsync(ex); }
            }
        }
    }

    private bool HasRunningDiagnostic() => running.Keys.Any(IsDiagnosticOperation);

    private static bool IsDiagnosticOperation(MaintenanceServiceOperation operation) => operation is
        MaintenanceServiceOperation.FullCheck or MaintenanceServiceOperation.QuickCheck or
        MaintenanceServiceOperation.DailySmartCheck or MaintenanceServiceOperation.ScheduledFullCheck;

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
            try
            {
                if (DateTimeOffset.UtcNow - lastTemperatureProbe >= TimeSpan.FromSeconds(30))
                {
                    await RefreshTemperatureAsync(cancellationToken);
                    lastTemperatureProbe = DateTimeOffset.UtcNow;
                }
                var snapshot = await new WindowsDiagnosticsRunner().RunLiveAsync(cancellationToken);
                var critical = EvaluateMonitoring(snapshot);
                await WriteStatusAsync(snapshot, critical.Count > 0 ? "Обнаружено критическое состояние" : "Мониторинг работает", cancellationToken, critical);
                if (critical.Count > 0)
                    await QueueMonitoringReportAsync(snapshot, critical, cancellationToken);
                var connected = File.Exists(Path.Combine(DataRoot, "server-device.json"));
                if (connected && DateTimeOffset.UtcNow - lastUpload >= TimeSpan.FromMinutes(5))
                {
                    StartOperation(MaintenanceServiceOperation.UploadReports);
                    lastUpload = DateTimeOffset.UtcNow;
                }
                if (connected && DateTimeOffset.UtcNow - lastCatalogRefresh >= TimeSpan.FromMinutes(15))
                {
                    StartOperation(MaintenanceServiceOperation.RefreshSoftwareCatalog);
                    lastCatalogRefresh = DateTimeOffset.UtcNow;
                }
                if (!HasFreshSmartReport())
                    StartOperation(MaintenanceServiceOperation.DailySmartCheck);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                await WriteServiceErrorAsync(ex);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private static bool HasFreshSmartReport()
    {
        try
        {
            var marker = Path.Combine(DataRoot, "last-smart-run.txt");
            if (!File.Exists(marker)) return false;
            return DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(), out var value) &&
                DateTimeOffset.UtcNow - value.ToUniversalTime() < TimeSpan.FromHours(24);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
    }

    private IReadOnlyList<CriticalMonitoringIssue> EvaluateMonitoring(ITSeti.Maintenance.Core.DiagnosticSnapshot snapshot)
    {
        var current = CriticalMonitoringEvaluator.Evaluate(snapshot);
        var active = current.Select(issue => issue.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in monitoringStreaks.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            monitoringStreaks.Remove(key);
            reportedMonitoringIssues.Remove(key);
        }

        var ready = new List<CriticalMonitoringIssue>();
        foreach (var issue in current)
        {
            monitoringStreaks[issue.Identity] = monitoringStreaks.TryGetValue(issue.Identity, out var count) ? count + 1 : 1;
            if (monitoringStreaks[issue.Identity] >= 2 && reportedMonitoringIssues.Add(issue.Identity)) ready.Add(issue);
        }
        return ready;
    }

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
                Rail5V = hwInfo.Rail5V
            } : null,
            snapshot.ActiveUptimeHours,
            snapshot.DiskReadLatencyMs,
            snapshot.DiskWriteLatencyMs,
            KindLabel = "Постоянный мониторинг",
            DiagnosticIssues = issues.Select(issue => new
            {
                issue.Title,
                issue.Detail,
                Severity = "Critical"
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
            var hwInfo = await HwInfoReader.ReadAsync(cancellationToken);
            var reading = hwInfo?.CpuTemperatureC is { } hwTemperature
                ? new CpuTemperatureReading(hwTemperature, $"HWiNFO · {hwInfo.Source}", hwInfo.CapturedAtUtc)
                : (await CpuTemperatureReader.ReadAsync()) with { CapturedAtUtc = DateTimeOffset.UtcNow };
            await WriteJsonAtomicallyAsync(CpuTemperatureCache.InstalledPath, reading, cancellationToken);
        }
        finally { temperatureLock.Release(); }
    }

    private Task WriteStatusAsync(ITSeti.Maintenance.Core.DiagnosticSnapshot? snapshot, string state,
        CancellationToken cancellationToken, IReadOnlyList<CriticalMonitoringIssue>? critical = null) => WriteJsonAtomicallyAsync(statusPath, new
        {
            Version = GetVersion(),
            State = state,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ComputerName = snapshot?.ComputerName,
            CpuPercent = snapshot?.CpuPercent is { } cpu ? (double?)Math.Round(cpu) : null,
            MemoryUsedPercent = snapshot is null ? null : (double?)Math.Round(snapshot.MemoryUsedPercent),
            TotalMemoryBytes = snapshot?.TotalMemoryBytes,
            AvailableMemoryBytes = snapshot?.AvailableMemoryBytes,
            CpuTemperatureC = snapshot?.CpuTemperatureC is { } temperature ? (double?)Math.Round(temperature) : null,
            UptimeHours = snapshot?.ActiveUptimeHours is { } uptime ? (double?)Math.Round(uptime, 1) : null,
            DiskReadLatencyMs = snapshot?.DiskReadLatencyMs is { } readLatency ? (double?)Math.Round(readLatency, 1) : null,
            DiskWriteLatencyMs = snapshot?.DiskWriteLatencyMs is { } writeLatency ? (double?)Math.Round(writeLatency, 1) : null,
            HwInfo = snapshot?.HwInfo,
            WindowsEdition = snapshot?.WindowsEdition,
            WindowsRelease = snapshot?.WindowsRelease,
            WindowsBuild = snapshot?.WindowsBuild,
            CriticalIssues = critical?.Select(issue => new { issue.Identity, issue.Title, issue.Detail }).ToArray() ?? [],
            FixedDisks = snapshot?.Disks.Select(disk => new
            {
                disk.Name,
                FreeBytes = disk.FreeBytes,
                TotalBytes = disk.TotalBytes
            }).ToArray(),
            RunningOperations = running.Keys.OrderBy(value => value).ToArray()
        }, cancellationToken);

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static async Task WriteServiceErrorAsync(Exception exception)
    {
        try
        {
            var directory = Path.Combine(DataRoot, "Service");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "service-error.log"),
                $"[{DateTimeOffset.Now:O}] {exception}\r\n", new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.3.3";

    private void StopChildren()
    {
        foreach (var process in running.Values)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        running.Clear();
    }

    public void Dispose()
    {
        StopChildren();
        temperatureLock.Dispose();
    }
}
