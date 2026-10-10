using System.Collections.Concurrent;
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
    private DateTimeOffset lastTemperatureProbe = DateTimeOffset.MinValue;
    private DateTimeOffset lastUpload = DateTimeOffset.MinValue;
    private DateTimeOffset lastCatalogRefresh = DateTimeOffset.MinValue;

    public MaintenanceServiceCoordinator(string? backendRootOverride = null, string? statusPathOverride = null)
    {
        backendRoot = backendRootOverride ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Backend"));
        statusPath = statusPathOverride ?? Path.Combine(DataRoot, "Service", "status.json");
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

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(MaintenanceServiceProtocol.PipeName, PipeDirection.InOut, 8,
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
                response = request is null ? new(false, "Некорректный запрос.", GetVersion()) : HandleRequest(request);
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

    private MaintenanceServiceResponse HandleRequest(MaintenanceServiceRequest request)
    {
        if (string.Equals(request.Command, "ping", StringComparison.OrdinalIgnoreCase))
            return new(true, "Служба обслуживания работает.", GetVersion());
        if (string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) && request.Operation is { } stopOperation)
            return new(true, StopOperation(stopOperation) ? "Операция остановлена." : "Операция уже завершена.", GetVersion());
        if (!string.Equals(request.Command, "start", StringComparison.OrdinalIgnoreCase) || request.Operation is null)
            return new(false, "Команда службы не поддерживается.", GetVersion());
        try
        {
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
            if (operation == MaintenanceServiceOperation.OrganizationSetup && HasPendingOrganizationRequest())
            {
                try { StartOperation(operation); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { await WriteServiceErrorAsync(ex); }
            }
        }
    }

    private static bool HasPendingOrganizationRequest()
    {
        try
        {
            var queue = Path.Combine(DataRoot, "OrganizationSetupRequests");
            return Directory.Exists(queue) && Directory.EnumerateFiles(queue, "*.json").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastTemperatureProbe >= TimeSpan.FromMinutes(1))
                {
                    await RefreshTemperatureAsync(cancellationToken);
                    lastTemperatureProbe = DateTimeOffset.UtcNow;
                }
                var snapshot = await new WindowsDiagnosticsRunner().RunLiveAsync(cancellationToken);
                await WriteStatusAsync(snapshot, "Мониторинг работает", cancellationToken);
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                await WriteServiceErrorAsync(ex);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
    }

    private async Task RefreshTemperatureAsync(CancellationToken cancellationToken)
    {
        if (!await temperatureLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var reading = (await CpuTemperatureReader.ReadAsync()) with { CapturedAtUtc = DateTimeOffset.UtcNow };
            await WriteJsonAtomicallyAsync(CpuTemperatureCache.InstalledPath, reading, cancellationToken);
        }
        finally { temperatureLock.Release(); }
    }

    private Task WriteStatusAsync(ITSeti.Maintenance.Core.DiagnosticSnapshot? snapshot, string state,
        CancellationToken cancellationToken) => WriteJsonAtomicallyAsync(statusPath, new
        {
            Version = GetVersion(),
            State = state,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            CpuPercent = snapshot?.CpuPercent is { } cpu ? (double?)Math.Round(cpu) : null,
            MemoryUsedPercent = snapshot is null ? null : (double?)Math.Round(snapshot.MemoryUsedPercent),
            CpuTemperatureC = snapshot?.CpuTemperatureC is { } temperature ? (double?)Math.Round(temperature) : null,
            UptimeHours = snapshot?.ActiveUptimeHours is { } uptime ? (double?)Math.Round(uptime, 1) : null,
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
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.3.1";

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
