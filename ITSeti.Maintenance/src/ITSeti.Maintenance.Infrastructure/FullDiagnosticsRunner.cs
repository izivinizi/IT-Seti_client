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
            using var launched = await Task.Run(() => Process.Start(start))
                ?? throw new InvalidOperationException("Windows не запустила программу");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (HasVisibleWindow(exe))
                    return $"{name}: окно открыто с правами администратора";
                if (launched.HasExited)
                    return $"{name}: программа завершилась без окна (код {launched.ExitCode}).";
                await Task.Delay(250);
            }
            return $"{name}: процесс запущен с правами администратора, окно не появилось за 5 секунд.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return $"{name}: запуск от администратора отменён в окне контроля учётных записей.";
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
    public Task<DiagnosticSnapshot> RunAsync(IProgress<DiagnosticProgress> progress) => RunCoreAsync(progress, false);
    public Task<DiagnosticSnapshot> RunUserAsync(IProgress<DiagnosticProgress> progress) => RunCoreAsync(progress, false);
    public Task<DiagnosticSnapshot> RunQuickAsync(IProgress<DiagnosticProgress> progress) => RunCoreAsync(progress, false, true);

    private async Task<DiagnosticSnapshot> RunCoreAsync(IProgress<DiagnosticProgress> progress, bool userMode, bool quickMode = false)
    {
        var installedTask = quickMode ? InstalledQuickTask : InstalledTask;
        if (IsInstalled && (!quickMode || HasInstalledQuickTask.Value))
        {
            try { return await AddCpuTemperatureAsync(await RunInstalledAsync(progress, installedTask)); }
            catch (InstalledTaskUnavailableException ex)
            {
                progress.Report(new($"Задача Windows недоступна ({ex.Message}). Проверка от текущей учётной записи"));
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
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
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
        progress.Report(new(userMode
            ? "Проверка запущена с правами текущего пользователя; SMART может быть недоступен"
            : "Проверка запущена без повышения прав; доступны только разрешённые текущей учётной записи данные"));
        var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить проверку");
        using (process)
        {
            var lastStage = "";
            var partialTimestamp = DateTime.MinValue;
            var lastSampleCount = -1;
            var diskLineCount = 0;
            while (!process.HasExited)
            {
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

        var reading = await CpuTemperatureReader.ReadAsync();
        return WithCpuTemperature(snapshot, reading.TemperatureC, reading.Status);
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

    private static async Task<DiagnosticSnapshot> RunInstalledAsync(IProgress<DiagnosticProgress> progress, string taskName)
    {
        var latest = Path.Combine(InstalledRoot, "latest.txt");
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
        if (launch.ExitCode != 0) throw new InstalledTaskUnavailableException($"Код {launch.ExitCode}: {(await errors).Trim()} {(await standard).Trim()}");

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
        if (root is null) throw new TimeoutException("Задача запущена, но новый каталог отчёта не появился. Проверьте планировщик задач.");

        var lastStage = "";
        var partialTimestamp = DateTime.MinValue;
        var lastSampleCount = -1;
        var diskLineCount = 0;
        deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = Path.Combine(root, "result.json");
            if (File.Exists(result))
            {
                var finalMessages = ReadDiskMessages(root, ref diskLineCount);
                if (finalMessages.Count > 0) progress.Report(new("Дисковый тест завершён", Messages: finalMessages));
                var snapshot = await ReadSnapshot(result);
                return snapshot;
            }
            var error = Path.Combine(root, "error.txt");
            if (File.Exists(error)) throw new InvalidOperationException(await File.ReadAllTextAsync(error));
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
        throw new TimeoutException($"Проверка не завершилась за 20 минут. Журнал: {root}");
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
