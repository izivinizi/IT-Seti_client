using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class FullDiagnosticsRunner(string? configuredToolsRoot = null) : IFullDiagnosticsRunner
{
    private static readonly string InstalledRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
    private const string InstalledTask = "ITSeti-Maintenance-Full";
    private const string InstalledQuickTask = "ITSeti-Maintenance-QuickFull";
    private static readonly Lazy<bool> HasInstalledTask = new(() =>
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, ArgumentList = { "/Query", "/TN", InstalledTask }
            });
            if (query is null) return false;
            if (!query.WaitForExit(3000)) { query.Kill(); return false; }
            return query.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { return false; }
    });
    private static readonly Lazy<bool> HasInstalledQuickTask = new(() =>
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, ArgumentList = { "/Query", "/TN", InstalledQuickTask }
            });
            if (query is null) return false;
            if (!query.WaitForExit(3000)) { query.Kill(); return false; }
            return query.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { return false; }
    });
    public static bool IsInstalled => File.Exists(Path.Combine(InstalledRoot, "installed.flag")) || HasInstalledTask.Value;
    public string? ToolsRoot { get; set; } = configuredToolsRoot;
    public async Task<string> LaunchInteractiveDiskToolAsync(bool diskInfo)
    {
        var root = ToolsRoot ?? FindTools(false);
        if (string.IsNullOrWhiteSpace(root)) return "Окна дисковых утилит: комплект не найден";
        var name = diskInfo ? "CrystalDiskInfo" : "CrystalDiskMark";
        var exe = ResolveInteractiveDiskTool(root, diskInfo);
        if (exe is null) return $"{name}: совместимый файл запуска не найден в {root}";
        try
        {
            if (HasVisibleWindow(exe)) return $"{name}: окно уже открыто. Закройте его перед повторным запуском от администратора.";
            var start = ElevatedProcessLauncher.CreateStartInfo(exe, Path.GetDirectoryName(exe)!);
            var launchContext = ElevatedProcessLauncher.IsCurrentProcessElevated
                ? "с правами администратора"
                : "без повышения прав";
            using var launched = await Task.Run(() => Process.Start(start))
                ?? throw new InvalidOperationException("Windows не запустила программу");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (HasVisibleWindow(exe))
                    return $"{name}: окно открыто {launchContext}";
                if (launched.HasExited)
                {
                    if (!ElevatedProcessLauncher.IsCurrentProcessElevated && launched.ExitCode == 0)
                        return $"{name}: программа завершилась без окна. Возможно, для запуска требуется токен администратора; UAC не запрашивался.";
                    return $"{name}: программа завершилась без окна (код {launched.ExitCode}).";
                }
                await Task.Delay(250);
            }
            return $"{name}: процесс запущен {launchContext}, окно не появилось за 5 секунд.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return $"{name}: запуск от администратора отменён в окне контроля учётных записей.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            return $"{name}: Windows требует права администратора для этого окна. UAC не запрашивался; фоновая проверка использует системную задачу.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"{name}: окно не открылось — {ex.Message}";
        }
    }

    public static string? ResolveInteractiveDiskTool(string root, bool diskInfo)
    {
        var candidates = diskInfo
            ? new[] { @"CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe", @"CrystalDiskInfo9_6_3_Portable\DiskInfoA64.exe" }
            : new[] { @"CrystalDiskMark9\DiskMark64.exe", @"CrystalDiskMark9\DiskMark64A.exe", @"CrystalDiskMark9\DiskMarkA64.exe" };
        return candidates.Select(relative => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
    }

    private static bool HasVisibleWindow(string exe)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (process)
            {
                try
                {
                    if (process.SessionId != current.SessionId || process.MainWindowHandle == IntPtr.Zero) continue;
                    try { if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
                    var isDiskMark = Path.GetFileNameWithoutExtension(exe).StartsWith("DiskMark", StringComparison.OrdinalIgnoreCase);
                    var title = isDiskMark ? "CrystalDiskMark" : "CrystalDiskInfo";
                    if (process.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        return false;
    }
    public Task<DiagnosticSnapshot> RunAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default) => RunCoreAsync(progress, false, false, cancellationToken);
    public Task<DiagnosticSnapshot> RunUserAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default) => RunCoreAsync(progress, true, false, cancellationToken);
    public Task<DiagnosticSnapshot> RunQuickAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default) => RunCoreAsync(progress, false, true, cancellationToken);
    public Task<DiagnosticSnapshot> RunUserQuickAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default) => RunCoreAsync(progress, true, true, cancellationToken);

    private async Task<DiagnosticSnapshot> RunCoreAsync(IProgress<DiagnosticProgress> progress, bool userMode, bool quickMode, CancellationToken cancellationToken)
    {
        var installedTask = quickMode ? InstalledQuickTask : InstalledTask;
        if (IsInstalled && (!quickMode || HasInstalledQuickTask.Value))
        {
            try { return await AddCpuTemperatureAsync(await RunInstalledAsync(progress, installedTask, cancellationToken)); }
            catch (InstalledTaskUnavailableException ex)
            {
                progress.Report(new(ElevatedProcessLauncher.IsCurrentProcessElevated
                    ? $"Задача Windows недоступна ({ex.Message}). Продолжаю проверку с правами администратора текущего окна."
                    : $"Задача Windows недоступна ({ex.Message}). Продолжаю проверку с правами текущей учётной записи."));
            }
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "Runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var backend = Path.Combine(AppContext.BaseDirectory, "Backend");
        foreach (var file in Directory.GetFiles(backend)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var tools = ToolsRoot ?? FindTools(true);
        var worker = Path.Combine(root, "FullCheckWorker.ps1");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(worker);
        info.ArgumentList.Add("-RunRoot");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add("-ToolsRoot");
        info.ArgumentList.Add(tools);
        info.ArgumentList.Add("-HeadlessDiskSpd");
        if (userMode) info.ArgumentList.Add("-UserMode");
        if (quickMode)
        {
            info.ArgumentList.Add("-SkipResourceSampling");
            info.ArgumentList.Add("-SkipDiskBenchmark");
        }
        var currentElevated = ElevatedProcessLauncher.IsCurrentProcessElevated;
        progress.Report(new(userMode
            ? currentElevated
                ? "Проверка запущена с правами администратора текущего окна; сбор выполняется без очистки"
                : "Проверка запущена с правами текущего пользователя; SMART может быть недоступен"
            : currentElevated
                ? "Проверка запущена с правами администратора текущего окна"
                : "Проверка запущена от текущей учётной записи; защищённые сведения могут быть недоступны"));
        var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить проверку");
        using (process)
        {
            var deadline = DateTime.UtcNow.AddMinutes(20);
            var lastStage = "";
            var partialTimestamp = DateTime.MinValue;
            var lastSampleCount = -1;
            var diskLineCount = 0;
            while (!process.HasExited)
            {
                if(cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new OperationCanceledException(cancellationToken);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException($"Проверка не завершилась за 20 минут. Журнал: {root}");
                }
                var stage = "Полная проверка выполняется…";
                try { stage = await File.ReadAllTextAsync(Path.Combine(root, "stage.txt")); } catch (IOException) { }
                var progressSnapshot = await ReadProgressSnapshotAsync(root, partialTimestamp);
                var partialChanged = progressSnapshot.Timestamp != partialTimestamp;
                partialTimestamp = progressSnapshot.Timestamp;
                var partial = progressSnapshot.Snapshot;
                var sampleCount = partial?.Full?.ResourceSampling?.Samples ?? -1;
                var messages = ReadDiskMessages(root, ref diskLineCount);
                if (stage != lastStage || partialChanged || partial is not null && sampleCount != lastSampleCount || messages.Count > 0)
                    progress.Report(new(stage, partial, messages));
                lastStage = stage;
                lastSampleCount = sampleCount;
                await Task.Delay(600);
            }
            var finalMessages = ReadDiskMessages(root, ref diskLineCount);
            cancellationToken.ThrowIfCancellationRequested();
            if (finalMessages.Count > 0) progress.Report(new("Дисковый тест завершён", Messages: finalMessages));
            var resultFile = Path.Combine(root, "result.json");
            if (File.Exists(resultFile)) return await AddCpuTemperatureAsync(await ReadSnapshot(resultFile));
            var errorFile = Path.Combine(root, "error.txt");
            var detail = File.Exists(errorFile) ? await File.ReadAllTextAsync(errorFile) : $"Код завершения: {process.ExitCode}. Журнал: {root}";
            throw new InvalidOperationException(detail);
        }
    }

    private static async Task<DiagnosticSnapshot> ReadSnapshot(string path) =>
        JsonSerializer.Deserialize<DiagnosticSnapshot>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("Пустой результат проверки");

    private static async Task<DiagnosticSnapshot> AddCpuTemperatureAsync(DiagnosticSnapshot snapshot)
    {
        var existing = snapshot.CpuTemperatureC ?? snapshot.Full?.CpuTemperatureC;
        if (existing is not null)
            return snapshot;

        var reading = await CpuTemperatureCache.RequestFreshAsync(TimeSpan.FromSeconds(5));
        if (reading is null && !IsInstalled)
            reading = await CpuTemperatureReader.ReadAsync();
        return WithCpuTemperature(snapshot, reading?.TemperatureC,
            reading?.Status ?? snapshot.CpuTemperatureStatus ?? "Системный опрос датчика не вернул свежие данные");
    }

    private static DiagnosticSnapshot WithCpuTemperature(DiagnosticSnapshot snapshot, double? temperature, string status) => snapshot with
    {
        CpuTemperatureC = temperature,
        CpuTemperatureStatus = status,
        Full = snapshot.Full is null ? null : snapshot.Full with { CpuTemperatureC = temperature }
    };

    private static IReadOnlyList<string> ReadDiskMessages(string root, ref int seen)
    {
        try
        {
            var path = Path.Combine(root, "disk-progress.log");
            if (!File.Exists(path)) return [];
            var lines = File.ReadAllLines(path);
            if (lines.Length < seen) seen = 0;
            var fresh = lines.Skip(seen).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            seen = lines.Length;
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static async Task<(DiagnosticSnapshot? Snapshot, DateTime Timestamp)> ReadProgressSnapshotAsync(string root, DateTime partialTimestamp)
    {
        var partialPath = Path.Combine(root, "partial.json");
        if (!File.Exists(partialPath)) return (null, partialTimestamp);
        try
        {
            var timestamp = File.GetLastWriteTimeUtc(partialPath);
            var snapshot = await ReadSnapshot(partialPath);
            return (await ApplyResourceProgressAsync(root, snapshot), timestamp);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return (null, partialTimestamp); }
    }

    private static async Task<DiagnosticSnapshot> ApplyResourceProgressAsync(string root, DiagnosticSnapshot snapshot)
    {
        var path = Path.Combine(root, "resource-sample-progress.json");
        if (!File.Exists(path)) return snapshot;
        try
        {
            var sample = JsonSerializer.Deserialize<ResourceSampleSummary>(await File.ReadAllTextAsync(path));
            if (sample is null || sample.Samples == 0 || snapshot.Full is null || snapshot.TotalMemoryBytes == 0) return snapshot;
            var available = (ulong)Math.Clamp(snapshot.TotalMemoryBytes * (1.0 - sample.MemoryAverage / 100.0), 0, snapshot.TotalMemoryBytes);
            return snapshot with
            {
                CpuPercent = sample.CpuAverage,
                AvailableMemoryBytes = available,
                Full = snapshot.Full with { ResourceSampling = sample }
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return snapshot; }
    }

    private static async Task<DiagnosticSnapshot> RunInstalledAsync(IProgress<DiagnosticProgress> progress, string taskName, CancellationToken cancellationToken)
    {
        var latest = GetInstalledReportPointer(taskName);
        var waitForCompletion = false;
        try { waitForCompletion = File.ReadAllText(GetInstalledScriptPath()).Contains("completed.txt", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var previous = File.Exists(latest) ? (await File.ReadAllTextAsync(latest)).Trim() : "";
        using var launch = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        launch.StartInfo.ArgumentList.Add("/Run");
        launch.StartInfo.ArgumentList.Add("/TN");
        launch.StartInfo.ArgumentList.Add(taskName);
        progress.Report(new("Запуск установленной проверки без запроса UAC"));
        try { launch.Start(); }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            throw new InstalledTaskUnavailableException(ex.Message, ex);
        }
        var standard = launch.StandardOutput.ReadToEndAsync();
        var errors = launch.StandardError.ReadToEndAsync();
        await launch.WaitForExitAsync();
        if (launch.ExitCode != 0) throw new InstalledTaskUnavailableException($"Код {launch.ExitCode}");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? root = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var current = (await File.ReadAllTextAsync(latest)).Trim();
                if (current != previous && Path.GetFullPath(current).StartsWith(Path.Combine(InstalledRoot, "Runs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    root = current;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException) { }
            await Task.Delay(500);
        }
        if (cancellationToken.IsCancellationRequested && root is null)
        {
            await StopInstalledTaskAsync(taskName);
            throw new OperationCanceledException(cancellationToken);
        }
        if (root is null) throw new TimeoutException("Задача запущена, но новый каталог отчёта не появился. Проверьте планировщик задач.");

        var ownership = $"owner:{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks}";
        try { await File.WriteAllTextAsync(Path.Combine(root, "cancel-request.txt"), ownership); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await CancelInstalledRunAsync(root, taskName);
            throw new InvalidOperationException("Не удалось привязать проверку к окну приложения; системная задача остановлена.", ex);
        }

        var lastStage = "";
        var partialTimestamp = DateTime.MinValue;
        var lastSampleCount = -1;
        var diskLineCount = 0;
        deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            if(cancellationToken.IsCancellationRequested)
            {
                await CancelInstalledRunAsync(root, taskName);
                throw new OperationCanceledException(cancellationToken);
            }
            if (File.Exists(Path.Combine(root, "cancelled.txt"))) throw new OperationCanceledException(cancellationToken);
            var error = Path.Combine(root, "error.txt");
            if (File.Exists(error)) throw new InvalidOperationException(await File.ReadAllTextAsync(error));
            var result = Path.Combine(root, "result.json");
            if (File.Exists(result) && (!waitForCompletion || File.Exists(Path.Combine(root, "completed.txt"))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalMessages = ReadDiskMessages(root, ref diskLineCount);
                if (finalMessages.Count > 0) progress.Report(new("Дисковый тест завершён", Messages: finalMessages));
                var snapshot = await ReadSnapshot(result);
                return snapshot;
            }
            var stage = "Полная проверка выполняется…";
            try { stage = await File.ReadAllTextAsync(Path.Combine(root, "stage.txt")); } catch (IOException) { }
            var progressSnapshot = await ReadProgressSnapshotAsync(root, partialTimestamp);
            var partialChanged = progressSnapshot.Timestamp != partialTimestamp;
            partialTimestamp = progressSnapshot.Timestamp;
            var partial = progressSnapshot.Snapshot;
            var sampleCount = partial?.Full?.ResourceSampling?.Samples ?? -1;
            var messages = ReadDiskMessages(root, ref diskLineCount);
            if (stage != lastStage || partialChanged || partial is not null && sampleCount != lastSampleCount || messages.Count > 0)
                progress.Report(new(stage, partial, messages));
            lastStage = stage;
            lastSampleCount = sampleCount;
            await Task.Delay(600);
        }
        await CancelInstalledRunAsync(root, taskName);
        throw new TimeoutException($"Проверка не завершилась за 20 минут. Журнал: {root}");
    }

    private static async Task CancelInstalledRunAsync(string root, string taskName)
    {
        var request = Path.Combine(root, "cancel-request.txt");
        try { await File.WriteAllTextAsync(request, "cancel"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(Path.Combine(root, "cancelled.txt"))) return;
            if (File.Exists(Path.Combine(root, "error.txt"))) return;
            if (File.Exists(Path.Combine(root, "completed.txt"))) return;
            await Task.Delay(500);
        }
        if (File.Exists(Path.Combine(root, "cancelled.txt")) || File.Exists(Path.Combine(root, "error.txt")) || File.Exists(Path.Combine(root, "completed.txt"))) return;
        await StopInstalledTaskAsync(taskName);
    }

    private static async Task StopInstalledTaskAsync(string taskName)
    {
        using var stop = Process.Start(new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/End", "/TN", taskName }
        });
        if (stop is null) throw new InvalidOperationException("Windows не остановила системную задачу проверки.");
        await stop.WaitForExitAsync();
        if (stop.ExitCode != 0) throw new InvalidOperationException("Windows не остановила системную задачу проверки (код " + stop.ExitCode + ").");
    }

    private static string GetInstalledReportPointer(string taskName)
    {
        var installedScript = GetInstalledScriptPath();
        try
        {
            if (File.ReadAllText(installedScript).Contains("latest-full.txt", StringComparison.Ordinal))
                return Path.Combine(InstalledRoot, taskName == InstalledQuickTask ? "latest-quick.txt" : "latest-full.txt");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.Combine(InstalledRoot, "latest.txt");
    }

    private static string GetInstalledScriptPath()
    {
        var installedScript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ITSeti Maintenance", "Backend", "InstalledCheck.ps1");
        return File.Exists(installedScript) ? installedScript : Path.Combine(AppContext.BaseDirectory, "Backend", "InstalledCheck.ps1");
    }

    public static async Task<IReadOnlyList<DiagnosticSnapshot>> ReadInstalledReportsAsync()
    {
        if (!IsInstalled) return [];
        var runs = Path.Combine(InstalledRoot, "Runs");
        if (!Directory.Exists(runs)) return [];
        var reports = new List<DiagnosticSnapshot>();
        foreach (var directory in Directory.EnumerateDirectories(runs)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(100))
        {
            var file = Path.Combine(directory, "result.json");
            if (!File.Exists(file)) continue;
            try { reports.Add(await ReadSnapshot(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return reports;
    }

    private static string FindTools(bool headless)
    {
        static bool HasDiskMark(string root) => ResolveInteractiveDiskTool(root, false) is not null;
        static bool HasDiskSpd(string root) => File.Exists(Path.Combine(root, "CrystalDiskMark9", "CdmResource", "DiskSpd", "DiskSpd64.exe"));
        static bool HasDiskInfo(string root) => ResolveInteractiveDiskTool(root, true) is not null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools");
        if ((headless ? HasDiskSpd(bundled) : HasDiskMark(bundled))
            && HasDiskInfo(bundled)) return bundled;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && (headless ? HasDiskSpd(drive.RootDirectory.FullName) : HasDiskMark(drive.RootDirectory.FullName))
                    && HasDiskInfo(drive.RootDirectory.FullName))
                    return drive.RootDirectory.FullName;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var caches = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServiceMaintenance", "ToolCache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServiceMaintenance", "ToolCache")
        };
        foreach (var cache in caches)
        {
            if (!Directory.Exists(cache)) continue;
            foreach (var directory in Directory.GetDirectories(cache).OrderDescending())
                if (File.Exists(Path.Combine(directory, "cache-id.txt")) && (headless ? HasDiskSpd(directory) : HasDiskMark(directory))
                    && HasDiskInfo(directory)) return directory;
        }
        return "";
    }

    private sealed class InstalledTaskUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
}
