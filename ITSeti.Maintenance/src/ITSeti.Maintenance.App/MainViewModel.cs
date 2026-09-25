using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Windows.Data;
using Microsoft.Win32;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public sealed record CheckProgressEntry(string Time, string Source, string Message);
public sealed record NetworkAdapterOverview(string Name, string Type, string Status, string LinkSpeed, string Addresses);
public sealed record DiskOverviewGroup(string Model, string MediaType, string Health, string TransferMode,
    IReadOnlyList<DiskSnapshot> Partitions, long? PowerOnHours = null)
{
    public bool HasPowerOnHours => PowerOnHours.HasValue;
    public string LifetimeLabel => DiskLifetime.Format(PowerOnHours);
    public string LifetimeWarning => DiskLifetime.ExceedsWarning(PowerOnHours) ? "Наработка выше 60 000 часов" : "";
}

public sealed class MainViewModel(IDiagnosticsRunner runner, IHistoryStore history, IFullDiagnosticsRunner? fullRunner = null, UserCleanupRunner? cleanupRunner = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DiagnosticSnapshot> History { get; } = [];
    public ObservableCollection<DiskSnapshot> Disks { get; } = [];
    public ObservableCollection<PhysicalDiskDetails> PhysicalDisks { get; } = [];
    public ObservableCollection<SmartDiskDetails> SmartDisks { get; } = [];
    public ObservableCollection<DiskOverviewGroup> DiskGroups { get; } = [];
    public ObservableCollection<NetworkAdapterOverview> NetworkAdapters { get; } = [];
    public ObservableCollection<ProcessDetails> Processes { get; } = [];
    public ObservableCollection<EventDetails> Events { get; } = [];
    public ObservableCollection<DebugStep> DebugSteps { get; } = [];
    public ObservableCollection<DebugFile> DebugFiles { get; } = [];
    public ObservableCollection<CheckProgressEntry> CheckProgressEntries { get; } = [];
    public ObservableCollection<string> TreeSizeVolumes { get; } = [];
    private string? selectedTreeSizeVolume;
    public string? SelectedTreeSizeVolume
    {
        get => selectedTreeSizeVolume;
        set { selectedTreeSizeVolume = value; Notify(); }
    }
    public bool CanLaunchTreeSize => !busy && !string.IsNullOrWhiteSpace(SelectedTreeSizeVolume);
    private bool checkProgressActive;
    private int checkProgressGeneration;
    private string checkProgressPhase = "Проверка ещё не запускалась";
    public string CheckProgressPhase => checkProgressPhase;
    public bool CheckProgressIsRunning => checkProgressActive;
    private void RecordProgress(string source, string message, int? generation = null)
    {
        if (!checkProgressActive || generation.HasValue && generation.Value != checkProgressGeneration || string.IsNullOrWhiteSpace(message)) return;
        message = message.Trim();
        if (CheckProgressEntries.LastOrDefault() is { } last && last.Source == source && last.Message == message) return;
        CheckProgressEntries.Add(new(DateTime.Now.ToString("HH:mm:ss"), source, message));
        Notify();
    }
    private static string FormatDiskProgress(string message)
    {
        if (message == "CrystalDiskInfo: SMART export started") return "CrystalDiskInfo: получение SMART";
        if (message.StartsWith("CrystalDiskInfo: SMART data received for ", StringComparison.Ordinal))
            return "CrystalDiskInfo: состояние накопителей получено";
        var match = System.Text.RegularExpressions.Regex.Match(message, @"^DiskSpd: (read|write), pass (\d)/2 - (started|[\d.,]+ MB/s)$");
        if (match.Success)
        {
            var operation = match.Groups[1].Value == "read" ? "чтение" : "запись";
            var suffix = match.Groups[3].Value == "started" ? "запущено" : match.Groups[3].Value.Replace("MB/s", "МБ/с");
            return $"DiskSpd: {operation}, проход {match.Groups[2].Value}/2 — {suffix}";
        }
        return message.Replace("DiskSpd: finished - read ", "DiskSpd: итог — чтение ")
            .Replace(" MB/s, write ", " МБ/с, запись ").Replace(" MB/s", " МБ/с");
    }
    private readonly DiagnosticDebugView debugView = new();
    private string? lastFullError;
    private string? currentFullStage;
    private bool debugPreferSelected;
    public string DebugSource => debugView.Source;
    public string DebugStage => debugView.Stage;
    public string DebugError => debugView.Error;
    public string DebugErrorLine => DebugError == "Ошибок запуска не обнаружено" ? "" : "Ошибка запуска: " + DebugError;
    public string DebugDirectory => debugView.DirectoryPath;
    public bool CanOpenDebugDirectory => Directory.Exists(DebugDirectory);
    public void RefreshDebug()
    {
        debugView.Refresh(Selected, debugPreferSelected ? null : lastFullError, debugPreferSelected ? null : currentFullStage, debugPreferSelected);
        Replace(DebugSteps, debugView.Steps);
        Replace(DebugFiles, debugView.Files);
        Notify();
    }
    public ICollectionView FilteredEvents => CollectionViewSource.GetDefaultView(Events);
    private int eventFilterLevel = 1;
    public int EventFilterLevel
    {
        get => eventFilterLevel;
        set { eventFilterLevel = value; FilteredEvents.Refresh(); SelectedEvent = FilteredEvents.Cast<EventDetails>().FirstOrDefault(); Notify(); }
    }
    public ObservableCollection<OrganizationComponent> SetupComponents { get; } = [];
    private string? setupSource;
    private bool setupAcceptanceMode;
    private string softwareActionStatus = "";
    private readonly GitHubReleaseClient applicationReleaseClient = new();
    private ApplicationRelease? availableApplicationRelease;
    private bool applicationUpdateCheckRunning;
    private bool applicationUpdateRunning;
    private string applicationUpdateStatus = "Проверка обновлений ещё не выполнялась";
    public bool SetupAcceptanceMode => setupAcceptanceMode;
    public bool HasSetupSource => setupSource is not null;
    public bool CanLaunchDiskTools => fullRunner is FullDiagnosticsRunner;
    public string SoftwareActionStatus => softwareActionStatus;
    public string SetupSource => setupSource ?? "Комплект ITSETI-Setup не найден";
    public string? SetupSourcePath => setupSource;
    public void SetSetupStatus(string message) { status = message; Notify(); }
    public void SetSoftwareActionStatus(string message) { softwareActionStatus = message; Notify(); }
    public string SetupSummary => $"Требуют внимания: {SetupComponents.Count(c => c.NeedsAttention)} из {SetupComponents.Count}";
    public string SetupWarning
    {
        get
        {
            if (SetupComponents.Count == 0) return "ПО: проверка не выполнялась";
            var missing = SetupComponents.Where(c => c.NeedsAttention && c.Name != "Локальная учётка и права").ToArray();
            return missing.Length > 0 ? "ПО: проверьте " + string.Join(", ", missing.Select(c => c.Name)) + "."
                : "ПО: основные компоненты обнаружены.";
        }
    }
    private EventDetails? selectedEvent;
    private string? resolvedEventMessage;
    private readonly WindowsEventMessageResolver eventResolver = new();
    private DiagnosticSnapshot? selected;
    private DiagnosticSnapshot? live;
    private bool busy;
    private int liveRefreshRunning;
    private DateTimeOffset nextTemperatureProbeUtc;
    private bool cleanupRunning;
    private bool repairRunning;
    private bool windowsUpdateActionRunning;
    private string? cleanupResult;
    private string? repairResult;
    private string windowsUpdateStatus = "Проверка обновлений не запускалась";
    private string windowsUpdatePolicyStatus = "Автоматические обновления не изменялись приложением";
    private string status = "Загрузка истории…";
    private string? userStatusOverride;
    private readonly MachineIdentityStore identityStore = new();
    private MachineIdentity identity = new(null, null, []);
    private string adminInventoryInput = "";
    public string AdminInventoryInput { get => adminInventoryInput; set { adminInventoryInput = value; Notify(); } }
    public string UserInventoryLabel => identity.InventoryNumber is { } number ? $"Инв. № {number}" : "Инв. № не указан";
    public string UserRmsLabel => identity.RmsId is { } id ? $"RMS: {id}" : "RMS: не найден";
    public string? InventoryNumber => identity.InventoryNumber;
    public string? RmsId => identity.RmsId;
    public bool HasInventoryNumber => identity.InventoryNumber is not null;
    public bool HasRmsId => identity.RmsId is not null;
    public string UserIpLabel => identity.IpAddresses.Count > 0
        ? "IP: " + string.Join(", ", identity.IpAddresses.Take(3)) + (identity.IpAddresses.Count > 3 ? $" (+{identity.IpAddresses.Count - 3})" : "")
        : "IP: нет подключения";
    public string UserIpTooltip => identity.IpAddresses.Count > 0 ? string.Join(Environment.NewLine, identity.IpAddresses) : "IP-адреса не обнаружены";
    public string UserUptimeLabel => UserSnapshot?.LastBootAt is { } boot
        ? $"Последний запуск Windows: {boot.LocalDateTime:dd.MM.yyyy HH:mm}" : "";
    public void RefreshIdentity()
    {
        identity = identityStore.Read();
        AdminInventoryInput = identity.InventoryNumber ?? "";
        Notify();
    }
    public void SaveInventoryNumber()
    {
        identityStore.SaveInventoryNumber(adminInventoryInput);
        RefreshIdentity();
        status = "Инвентарный номер сохранён для всех пользователей этого ПК";
        Notify();
    }


    public DiagnosticSnapshot? Selected
    {
        get => selected;
        set
        {
            if (!busy && selected is not null && value?.Id != selected.Id) debugPreferSelected = true;
            selected = value;
            Disks.Clear();
            if (value is not null) foreach (var disk in value.Disks) Disks.Add(disk);
            Replace(PhysicalDisks, value?.Full?.PhysicalDisks ?? value?.QuickDisks);
            Replace(SmartDisks, value?.Full?.SmartDisks);
            Replace(DiskGroups, BuildDiskGroups(value));
            Replace(Processes, value?.Full?.Processes);
            Replace(Events, value?.Full?.Events.OrderBy(e => e.Level).ThenByDescending(e => e.Time));
            FilteredEvents.Filter = item => item is EventDetails detail && detail.Level <= eventFilterLevel;
            SelectedEvent = FilteredEvents.Cast<EventDetails>().FirstOrDefault();
            RefreshDebug();
            Notify();
        }
    }
    private static IReadOnlyList<DiskOverviewGroup> BuildDiskGroups(DiagnosticSnapshot? snapshot)
    {
        if (snapshot is null) return [];
        var physical = snapshot.Full?.PhysicalDisks ?? snapshot.QuickDisks ?? [];
        var smart = snapshot.Full?.SmartDisks ?? [];
        var groups = new List<DiskOverviewGroup>();
        var usedSmart = new HashSet<SmartDiskDetails>();
        var assignedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Normalize(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"[^\p{L}\p{Nd}]", "").ToUpperInvariant();
        static string[] Letters(SmartDiskDetails? disk) => disk is null ? [] :
            System.Text.RegularExpressions.Regex.Matches(disk.Letters ?? "", @"(?i)(?<![A-Z])[A-Z]:")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.ToUpperInvariant()).Distinct().ToArray();

        List<DiskSnapshot> VolumesFor(SmartDiskDetails? disk)
        {
            var letters = Letters(disk);
            var matches = snapshot.Disks.Where(volume => !assignedVolumes.Contains(volume.Name) &&
                letters.Contains(volume.Name.TrimEnd('\\').ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var volume in matches) assignedVolumes.Add(volume.Name);
            return matches;
        }

        foreach (var drive in physical)
        {
            var model = Normalize(drive.Model);
            var match = smart.FirstOrDefault(item => !usedSmart.Contains(item) &&
                (string.Equals(model, Normalize(item.Model), StringComparison.OrdinalIgnoreCase) ||
                 model.Length >= 8 && Normalize(item.Model).Length >= 8 &&
                 (model.Contains(Normalize(item.Model), StringComparison.OrdinalIgnoreCase) || Normalize(item.Model).Contains(model, StringComparison.OrdinalIgnoreCase))));
            if (match is not null) usedSmart.Add(match);
            var partitions = VolumesFor(match);
            if (partitions.Count == 0 && physical.Count == 1 && smart.Count <= 1)
            {
                partitions = snapshot.Disks.ToList();
                foreach (var volume in partitions) assignedVolumes.Add(volume.Name);
            }
            var media = !string.IsNullOrWhiteSpace(drive.MediaType) && drive.MediaType != "Unknown" ? drive.MediaType : match?.MediaType ?? "Не определён";
            var health = match is null ? $"Windows: {drive.Health}" : $"SMART: {match.Status} · Windows: {drive.Health}";
            groups.Add(new(drive.Model, media, health, match?.TransferMode ?? "", partitions, match?.PowerOnHours));
        }
        foreach (var item in smart.Where(item => !usedSmart.Contains(item)))
            groups.Add(new(item.Model, item.MediaType, "SMART: " + item.Status, item.TransferMode, VolumesFor(item), item.PowerOnHours));

        var unmatched = snapshot.Disks.Where(volume => !assignedVolumes.Contains(volume.Name)).ToList();
        if (unmatched.Count > 0)
            groups.Add(new("Накопитель не определён", "", "Windows-разделы; привязка к физическому диску не получена", "", unmatched));
        return groups;
    }
    public string ComputerName => Environment.MachineName;
    private static readonly string CurrentAccountContext = ReadCurrentAccountContext();
    public string CurrentAccountSummary => CurrentAccountContext;
    private static string ReadCurrentAccountContext()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var token = principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? "администраторский токен активен"
                : "обычный токен; админские операции выполняются служебной задачей";
            return $"{identity.Name} · {token}";
        }
        catch
        {
            return "Контекст текущей учётной записи определить не удалось";
        }
    }
    private DiagnosticSnapshot? UserSnapshot => live ?? Selected;
    private DiagnosticSnapshot? UserDiagnosticSnapshot => Selected ?? live;
    private DiskSnapshot? SystemDisk => UserSnapshot?.Disks.FirstOrDefault(d => d.Name.StartsWith(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase))
        ?? UserSnapshot?.Disks.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
    private DiskSnapshot? LowSpaceDisk => UserSnapshot?.Disks.Where(d => d.TotalBytes > 0 && d.FreeBytes < 15L * 1073741824)
        .OrderBy(d => d.FreeBytes).FirstOrDefault();
    public bool HasLowDiskSpace => LowSpaceDisk is not null;
    public string LowSpaceActionLabel => LowSpaceDisk is { } disk ? $"Что занимает {disk.Name.TrimEnd('\\')}" : "Что занимает место";
    public double UserCpuValue => Math.Clamp(UserSnapshot?.CpuPercent ?? 0, 0, 100);
    public double UserMemoryValue => Math.Clamp(UserSnapshot?.MemoryUsedPercent ?? 0, 0, 100);
    public double UserDiskValue => Math.Clamp(SystemDisk?.UsedPercent ?? 0, 0, 100);
    public string UserCpuLabel => UserSnapshot?.CpuLabel ?? "—";
    public string UserCpuDetail => UserDiagnosticSnapshot?.Full?.ResourceSampling is { Samples: >= 6, Error: "" } sample
        ? $"Средняя за 30 с: {sample.CpuAverage:N0}%"
        : busy && UserDiagnosticSnapshot?.Full?.ResourceSampling is { Samples: > 0, Error: "" } liveSample
            ? $"Среднее по текущим замерам: {liveSample.CpuAverage:N0}%" : "";
    private static readonly string LocalCpuName = ReadLocalCpuName();
    private static string ReadLocalCpuName()
    {
        try { return Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Модель процессора не определена"; }
        catch { return "Модель процессора не определена"; }
    }
    public string UserCpuName => UserDiagnosticSnapshot?.Full?.CpuName is { Length: > 0 } name ? name : LocalCpuName;
    public string UserMemoryLabel => UserSnapshot?.MemoryLabel ?? "—";
    public string UserDiskLabel => SystemDisk?.Usage ?? "—";
    public string UserDiskDetail => SystemDisk is { } disk ? $"{disk.Name} · свободно {disk.FreeBytes / 1073741824.0:N1} ГБ" : "Нет данных о диске";
    public string UserDiskHealth
    {
        get
        {
            var full = UserDiagnosticSnapshot?.Full;
            string state;
            if (full is not null)
            {
                if (full.SmartDisks.Any(d => System.Text.RegularExpressions.Regex.IsMatch(d.Status, "Caution|Bad|Тревог|Плох", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    || full.PhysicalDisks.Any(d => System.Text.RegularExpressions.Regex.IsMatch(d.Health, "Warning|Unhealthy|Degraded|Pred Fail|Error|Тревог|Плох", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    state = "Состояние диска требует внимания";
                else if (UserSystemSmartDisk is { Status.Length: > 0 }) state = "Состояние диска: норма";
                else if (full.SmartDisks.Count > 0) state = "Состояние SMART получено";
                else if (full.PhysicalDisks.Count > 0) state = "Состояние проверено Windows";
                else state = "Состояние диска не получено";
            }
            else
            {
                var quick = UserDiagnosticSnapshot?.QuickDisks;
                if (quick?.Any(d => System.Text.RegularExpressions.Regex.IsMatch(d.Health, "Warning|Unhealthy|Degraded|Pred Fail|Error|Тревог|Плох", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) == true)
                    state = "Windows сообщает о проблеме с диском";
                else state = quick is { Count: > 0 } ? "Состояние проверено по данным Windows" : "Состояние диска не проверено";
            }
            if (UserSystemSmartDisk?.PowerOnHours is long hours)
                state += " · наработка " + DiskLifetime.Format(hours);
            return state;
        }
    }
    private SmartDiskDetails? UserSystemSmartDisk
    {
        get
        {
            var full = UserDiagnosticSnapshot?.Full;
            if (full is null) return null;
            var drive = SystemDisk?.Name.TrimEnd('\\') ?? (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
            return full.SmartDisks.FirstOrDefault(d =>
                System.Text.RegularExpressions.Regex.Matches(d.Letters ?? "", @"(?i)(?<![A-Z])[A-Z]:")
                    .Cast<System.Text.RegularExpressions.Match>().Any(m => string.Equals(m.Value, drive, StringComparison.OrdinalIgnoreCase)));
        }
    }
    public string UserCpuNameAndTemperature => $"{UserCpuName} · {FormatCpuTemperature(UserSnapshot?.CpuTemperatureC ?? UserSnapshot?.Full?.CpuTemperatureC)}";
    public string UserMemoryDetail
    {
        get
        {
            if (UserSnapshot is not { TotalMemoryBytes: > 0 } current) return "Нет данных о памяти";
            var text = $"Свободно {current.AvailableMemoryBytes / 1073741824.0:N1} ГБ";
            if (UserDiagnosticSnapshot?.Full?.ResourceSampling is { Samples: >= 6, Error: "" } sample)
                text += $" · 30 с: {sample.MemoryAverage:N0}%";
            else if (busy && UserDiagnosticSnapshot?.Full?.ResourceSampling is { Samples: > 0, Error: "" } liveSample)
                text += $" · среднее: {liveSample.MemoryAverage:N0}%";
            return text;
        }
    }
    public string UserLiveSummary
    {
        get
        {
            if (!busy) return "";
            if (UserSnapshot is not { } snapshot || snapshot.TotalMemoryBytes == 0) return "Собираем первые показатели…";
            var systemDisk = snapshot.Disks.FirstOrDefault(d => d.Name.StartsWith(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase));
            var parts = new List<string> { $"ЦП {snapshot.CpuLabel}", $"ОЗУ {snapshot.MemoryLabel}" };
            if (systemDisk is not null) parts.Add($"{systemDisk.Name.TrimEnd('\\')} свободно {systemDisk.FreeBytes / 1073741824.0:N1} ГБ");
            if (snapshot.Full is { } full && full.Processes.Count > 0) parts.Add($"процессов вне списка: {full.Processes.Count}");
            return string.Join(" · ", parts);
        }
    }
    public IReadOnlyList<UserIssue> UserIssues => DiagnosticRules.GetUserIssues(UserDiagnosticSnapshot ?? new DiagnosticSnapshot(Guid.Empty, DateTimeOffset.MinValue, "", -1, 0, 0, [], []));
    public IReadOnlyList<UserIssue> UserVisibleIssues => UserIssues.Take(2).ToArray();
    public bool HasMoreUserIssues => UserIssues.Count > 2;
    public string MoreUserIssuesLabel => $"Все причины ({UserIssues.Count})";
    public string UserIssueHeading => UserDiagnosticSnapshot is null ? "Результаты проверки" : UserIssues.Count > 0 ? "Что может мешать работе" : UserCoverage.Length > 0 ? "Проверено не всё" : "Проблем не обнаружено";
    public string UserIssueDetail => Selected is null && live is null ? "Проверка ещё не выполнялась" : UserIssues.Count > 0 ? $"Найдено {UserIssues.Count} возможных причин" : UserCoverage.Length > 0 ? "Доступные показатели без замечаний" : "По измеренным показателям замечаний нет";
    public string UserCoverage
    {
        get
        {
            var full = UserDiagnosticSnapshot?.Full;
            if (full is null) return Selected is not null && UserDiagnosticSnapshot is { QuickDisks: not { Count: > 0 } }
                ? "Не удалось проверить состояние дисков. Подробности доступны инженеру." : "";
            var lines = new List<string>();
            if (full.Benchmark is { State: "Failed" or "Skipped" }) lines.Add("скорость диска");
            if (full.SmartDisks.Count == 0) lines.Add("подробное состояние диска");
            if (full.ResourceSampling is not { Samples: >= 6, Error: "" }) lines.Add("длительная нагрузка");
            return lines.Count == 0 ? "" : "Не удалось проверить: " + string.Join(", ", lines) + ". Подробности доступны инженеру.";
        }
    }
    public string UserStatus => userStatusOverride ?? status;

    public void RefreshSetupAudit(string? source = null)
    {
        setupSource = source ?? OrganizationSoftwareAudit.FindSource();
        SetupComponents.Clear();
        foreach (var component in OrganizationSoftwareAudit.Inspect(setupSource, setupAcceptanceMode)) SetupComponents.Add(component);
        Notify();
    }

    public void RefreshTreeSizeVolumes()
    {
        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
        var preferred = SelectedTreeSizeVolume ?? systemDrive;
        TreeSizeVolumes.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) TreeSizeVolumes.Add(drive.Name);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        SelectedTreeSizeVolume = TreeSizeVolumes.FirstOrDefault(d => string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase))
            ?? TreeSizeVolumes.FirstOrDefault(d => string.Equals(d, systemDrive, StringComparison.OrdinalIgnoreCase))
            ?? TreeSizeVolumes.FirstOrDefault();
    }

    public void SetSetupMode(bool acceptance)
    {
        setupAcceptanceMode = acceptance;
        RefreshSetupAudit(setupSource);
    }
    public string ScheduleStatus => FullDiagnosticsRunner.IsInstalled ? "Быстрая проверка: раз в 14 дней · полная с восстановлением: раз в 60 дней" : "Не настроено";
    public string ApplicationUpdateStatus => applicationUpdateStatus;
    public bool CanCheckApplicationUpdates => !busy && !applicationUpdateCheckRunning && !applicationUpdateRunning;
    public bool CanInstallApplicationUpdate => !busy && !applicationUpdateRunning && !applicationUpdateCheckRunning;
    public string FullRunHint => FullDiagnosticsRunner.IsInstalled ? "Запустить установленную проверку без UAC" : "Полная проверка: требуется подтверждение UAC";
    public bool CanRun => !busy;
    public bool CanRunFull => CanRun && fullRunner is not null;
    public bool CanRunQuickFull => CanRun && fullRunner is not null;
    public bool CanRunCleanup => !busy && !cleanupRunning;
    public bool CanRunRepair => !busy && !repairRunning;
    public bool CanCheckWindowsUpdates => !busy && !windowsUpdateActionRunning;
    public bool CanChangeWindowsUpdatePolicy => !busy && !windowsUpdateActionRunning;
    public string WindowsUpdateStatus => windowsUpdateStatus;
    public string WindowsUpdatePolicyStatus => windowsUpdatePolicyStatus;
    public bool IsBusy => busy;
    public bool IsBackgroundMaintenanceRunning => cleanupRunning || repairRunning;
    public string CleanupStatusDetails => cleanupResult ?? UserCleanupRunner.LatestSummary;
    public string RepairStatusDetails => repairResult ?? SystemRepairRunner.LatestStatus;
    public string CleanupStatus => BriefMaintenanceStatus(CleanupStatusDetails);
    public string StandaloneRepairStatus => BriefMaintenanceStatus(RepairStatusDetails);
    private static string BriefMaintenanceStatus(string value)
    {
        var firstLine = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Нет данных";
        return firstLine.Length <= 210 ? firstLine : firstLine[..207] + "...";
    }
    public bool CanOpenRepairLog => SystemRepairRunner.LatestLogPath is not null;
    public string? RepairLogPath => SystemRepairRunner.LatestLogPath;
    public string Status => status;
    public string Cpu => Selected?.CpuLabel ?? "—";
    public string CpuDetail => $"{Selected?.Full?.CpuName ?? LocalCpuName} · {FormatCpuTemperature(Selected?.CpuTemperatureC ?? Selected?.Full?.CpuTemperatureC)}";
    public string Gpu => Selected?.Full?.GpuName ?? "Нет данных";
    public string MemoryType => Selected?.Full?.MemoryType is { Length: > 0 } type ? type : "Тип DDR не определён";
    public string Memory => Selected?.MemoryLabel ?? "—";
    public string MemoryDetail => Selected is null || Selected.TotalMemoryBytes == 0 ? "Замер не выполнен"
        : $"Свободно {Selected.AvailableMemoryBytes / 1073741824.0:N1} из {Selected.TotalMemoryBytes / 1073741824.0:N1} ГБ";
    public string DiskCount => Selected?.Disks.Count.ToString() ?? "—";
    public string DiskDetail => Selected is null ? "Нет данных" : $"Разделов с нехваткой места: {Selected.Disks.Count(d => d.FreeBytes < 15L * 1073741824)}";
    public string SystemDiskSummary
    {
        get
        {
            if (Selected is null) return "Системный диск ещё не проверен";
            var drive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
            var volume = Selected.Disks.FirstOrDefault(d => string.Equals(d.Name.TrimEnd('\\'), drive, StringComparison.OrdinalIgnoreCase));
            var smart = Selected.Full?.SmartDisks.FirstOrDefault(d =>
                System.Text.RegularExpressions.Regex.Matches(d.Letters ?? "", @"(?i)(?<![A-Z])[A-Z]:")
                    .Cast<System.Text.RegularExpressions.Match>().Any(m => string.Equals(m.Value, drive, StringComparison.OrdinalIgnoreCase)));
            var physical = Selected.Full?.PhysicalDisks.FirstOrDefault(d =>
                smart is not null && string.Equals(d.Model, smart.Model, StringComparison.OrdinalIgnoreCase))
                ?? (Selected.Full?.PhysicalDisks.Count == 1 ? Selected.Full.PhysicalDisks[0] : null);
            var parts = new List<string> { drive };
            if (smart is not null) parts.Add(smart.Model);
            else if (physical is not null) parts.Add(physical.Model);
            var media = smart?.MediaType ?? physical?.MediaType;
            if (!string.IsNullOrWhiteSpace(media) && media != "Unknown") parts.Add(media);
            if (!string.IsNullOrWhiteSpace(smart?.Status)) parts.Add("SMART " + smart.Status);
            else if (!string.IsNullOrWhiteSpace(physical?.Health)) parts.Add(physical.Health);
            if (!string.IsNullOrWhiteSpace(smart?.TransferMode)) parts.Add(smart.TransferMode.Replace(" | ", "/"));
            if (smart?.PowerOnHours is long hours) parts.Add("наработка " + DiskLifetime.Format(hours));
            if (Selected.Full?.Benchmark is { State: "Completed" } benchmark
                && string.Equals(benchmark.Drive.TrimEnd('\\'), drive, StringComparison.OrdinalIgnoreCase))
                parts.Add($"SEQ чт/зп {benchmark.Read?.ToString("N0") ?? "—"}/{benchmark.Write?.ToString("N0") ?? "—"} МБ/с");
            if (volume is not null)
                parts.Add($"{volume.FreeBytes / 1073741824.0:N1}/{volume.TotalBytes / 1073741824.0:N1} ГБ свободно · занято {volume.UsedPercent:N0}%");
            else parts.Add("свободное место не измерено");
            return string.Join(" · ", parts);
        }
    }
    public string SnapshotDate => Selected is null ? "Сохранённых проверок пока нет" : $"Проверка от {Selected.DateLabel}";
    private static string FormatCpuTemperature(double? temperature) => temperature is >= 0 and <= 120
        ? $"{temperature:N0} °C" : "температура недоступна";

    public void RefreshNetworkAdapters()
    {
        NetworkAdapters.Clear();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(IsRelevantNetworkAdapter)
                         .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string addresses;
                try
                {
                    addresses = string.Join(", ", adapter.GetIPProperties().UnicastAddresses
                        .Select(item => item.Address)
                        .Where(IsUsableNetworkAddress)
                        .Select(address => address.ToString())
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }
                catch (NetworkInformationException) { continue; }
                if (string.IsNullOrWhiteSpace(addresses)) continue;
                var status = adapter.OperationalStatus switch
                {
                    OperationalStatus.Up => "Подключён",
                    OperationalStatus.Down => "Отключён",
                    OperationalStatus.Dormant => "Ожидание",
                    OperationalStatus.LowerLayerDown => "Нет нижнего канала",
                    _ => adapter.OperationalStatus.ToString()
                };
                var speed = adapter.OperationalStatus == OperationalStatus.Up && adapter.Speed > 0
                    ? adapter.Speed >= 1_000_000_000 ? $"{adapter.Speed / 1_000_000_000.0:N1} Гбит/с" : $"{adapter.Speed / 1_000_000.0:N0} Мбит/с"
                    : "—";
                var type = adapter.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Tunnel => "Туннель / VPN",
                    NetworkInterfaceType.Ppp => "PPP / VPN",
                    _ => adapter.NetworkInterfaceType.ToString()
                };
                NetworkAdapters.Add(new(adapter.Name, type, status, speed, addresses));
            }
        }
        catch (NetworkInformationException) { }
        Notify();
    }

    private static bool IsRelevantNetworkAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            return false;

        var identity = adapter.Name + " " + adapter.Description;
        var vpn = System.Text.RegularExpressions.Regex.IsMatch(identity,
            @"(?i)(radmin|wireguard|openvpn|tailscale|zerotier|proton|global.?protect|anyconnect|forti|pulse|sonicwall|checkpoint|citrix|softether|\bvpn\b|ikev2)");
        var noise = System.Text.RegularExpressions.Regex.IsMatch(identity,
            @"(?i)(hyper.?v|vmware|virtualbox|\bvbox\b|wsl|docker|loopback|teredo|isatap|wi.?fi direct|bluetooth|npcap|wan miniport|microsoft kernel debug)");
        if (noise && !vpn) return false;

        return adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.Wireless80211
            || vpn && adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ethernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.GigabitEthernet;
    }

    private static bool IsUsableNetworkAddress(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any)
            || address.Equals(System.Net.IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return !address.ToString().StartsWith("169.254.", StringComparison.Ordinal);
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }
    public string Findings
    {
        get
        {
            if (Selected is null) return "Нет результатов";
            if (Selected.Full is null)
            {
                var quickIssues = DiagnosticRules.GetUserIssues(Selected);
                return quickIssues.Count > 0
                    ? string.Join(Environment.NewLine, quickIssues.Select(i => $"{i.Title}. {i.Detail}"))
                    : "По проверенным показателям причин замедления не найдено. Это короткая проверка, а не оценка всего компьютера.";
            }
            var lines = DiagnosticRules.GetFindings(Selected).ToList();
            lines.AddRange(Selected.Notes);
            if (Selected.Full?.Benchmark is { State: "Failed" } test) lines.Add("Тест диска: " + test.Error);
            return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "По измеренным показателям замечаний нет";
        }
    }
    public string OverviewFindings
    {
        get
        {
            if (Selected is null) return "Проверка ещё не выполнялась";
            var issues = DiagnosticRules.GetUserIssues(Selected)
                .Where(issue => !issue.Title.Contains("обычном жёстком диске", StringComparison.OrdinalIgnoreCase))
                .Select(issue => issue.Title.StartsWith("На диске ", StringComparison.OrdinalIgnoreCase)
                    ? issue.Title + (Selected.Disks.FirstOrDefault(d => issue.Title.Contains(d.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is { } disk
                        ? $": свободно {disk.FreeBytes / 1073741824.0:N1} ГБ" : "")
                    : issue.Title == "Системный диск читает данные медленно" && Selected.Full?.Benchmark.Read is double read
                        ? $"Системный диск: чтение {read:N0} МБ/с" : issue.Title).ToList();
            if (Selected.Full is { } full)
            {
                issues.AddRange(full.Events.Where(e => e.Level == 1).Select(e => $"Критическое событие Windows: {e.Provider}, код {e.Id}.").Distinct().Take(3));
                if (full.Benchmark.State is "Failed" or "Skipped") issues.Add("Скорость диска: нет результата");
                if (full.SmartDisks.Count == 0) issues.Add("SMART: нет данных");
            }
            return issues.Count == 0 ? "Отклонений не обнаружено" : string.Join(Environment.NewLine, issues);
        }
    }
    public string HistoryCount => $"Сохранено проверок: {History.Count}";
    public EventDetails? SelectedEvent
    {
        get => selectedEvent;
        set
        {
            selectedEvent = value;
            resolvedEventMessage = null;
            Notify();
            if (value is not null && value.Message == "Описание не получено для этого события." && value.RecordId > 0)
                _ = ResolveMessageAsync(value);
        }
    }
    private async Task ResolveMessageAsync(EventDetails item)
    {
        resolvedEventMessage = "Чтение описания события…"; Notify();
        var message = await eventResolver.GetMessageAsync(item);
        if (selectedEvent == item) { resolvedEventMessage = message; Notify(); }
    }
    public string EventMessage => resolvedEventMessage ?? SelectedEvent?.Message ?? "Событие не выбрано";
    public string Coverage => Selected?.Full is not { } full
        ? Selected?.QuickDisks is { Count: > 0 }
            ? "Состояние накопителей показано по данным Windows. Подробная проверка дисков, запущенных программ и событий доступна в полной диагностике."
            : "Состояние накопителей определить не удалось. Подробная проверка дисков, запущенных программ и событий доступна в полной диагностике."
        : $"SMART: {full.SmartDisks.Count} дисков · Журналы: {full.Events.Count} событий"
            + (full.EventLimited || full.EventUnavailable > 0 ? " · Неполная выборка" : "")
            + (full.SmartDisks.Count == 0 ? " · SMART не получен" : "");
    public string RepairStatus
    {
        get
        {
            var root = Selected?.Full?.ReportDirectory;
            if (string.IsNullOrEmpty(root)) return "Нет полной проверки";
            try
            {
                var repair = Path.Combine(root, "repair");
                var statusFile = Path.Combine(repair, "status.txt");
                if (File.Exists(statusFile)) return File.ReadAllText(statusFile).Trim();
                return File.Exists(Path.Combine(repair, "started.txt")) ? "Восстановление запущено в фоне" : "Восстановление не запускалось";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Статус недоступен: " + ex.Message; }
        }
    }
    public void RefreshRepairStatus() => Notify();
    public void RefreshMaintenanceStatus() => Notify();

    public async Task CheckWindowsUpdatesAsync()
    {
        if (busy || windowsUpdateActionRunning) return;
        windowsUpdateActionRunning = true;
        windowsUpdateStatus = "Идёт поиск обновлений Windows…";
        Notify();
        try { windowsUpdateStatus = await new WindowsUpdateCheckRunner().CheckAsync(); }
        catch (Exception ex) { windowsUpdateStatus = "Не удалось проверить обновления: " + ex.Message; }
        finally { windowsUpdateActionRunning = false; Notify(); }
    }

    public async Task CheckApplicationUpdatesAsync(bool automatic = false)
    {
        if (applicationUpdateCheckRunning || applicationUpdateRunning || busy && !automatic) return;
        applicationUpdateCheckRunning = true;
        availableApplicationRelease = null;
        applicationUpdateStatus = "Проверяем GitHub…";
        Notify();
        try
        {
            var current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0);
            availableApplicationRelease = await applicationReleaseClient.GetUpdateAsync(current);
            applicationUpdateStatus = availableApplicationRelease is null
                ? "Установлена последняя версия приложения."
                : $"Доступна версия {availableApplicationRelease.Version}.";
        }
        catch (Exception ex)
        {
            applicationUpdateStatus = automatic
                ? "GitHub недоступен. Проверку обновлений можно повторить во вкладке ПО."
                : "Не удалось проверить обновления: " + ex.Message;
        }
        finally
        {
            applicationUpdateCheckRunning = false;
            Notify();
        }
    }

    public async Task<bool> RequestApplicationUpdateAsync()
    {
        if (busy || applicationUpdateRunning || availableApplicationRelease is null) return false;
        applicationUpdateRunning = true;
        applicationUpdateStatus = "Передаём запрос системной задаче…";
        Notify();
        try
        {
            await new ApplicationUpdateRunner().RequestUpdateAsync();
            applicationUpdateStatus = "Обновление запущено. Приложение будет закрыто.";
            return true;
        }
        catch (Exception ex)
        {
            applicationUpdateStatus = "Не удалось запустить обновление: " + ex.Message;
            return false;
        }
        finally
        {
            applicationUpdateRunning = false;
            Notify();
        }
    }

    public Task DisableWindowsAutomaticUpdatesAsync() => RunWindowsUpdatePolicyAsync(disable: true);
    public Task RestoreWindowsAutomaticUpdatesAsync() => RunWindowsUpdatePolicyAsync(disable: false);

    private async Task RunWindowsUpdatePolicyAsync(bool disable)
    {
        if (busy || windowsUpdateActionRunning) return;
        windowsUpdateActionRunning = true;
        windowsUpdatePolicyStatus = disable ? "Отключаем автоматические обновления…" : "Возвращаем сохранённую настройку…";
        Notify();
        try
        {
            var runner = new WindowsUpdatePolicyRunner();
            windowsUpdatePolicyStatus = disable ? await runner.DisableAsync() : await runner.RestoreAsync();
        }
        catch (Exception ex) { windowsUpdatePolicyStatus = "Не удалось изменить автообновления: " + ex.Message; }
        finally { windowsUpdateActionRunning = false; Notify(); }
    }
    public string ProcessStatus => Selected?.Full is { } f
        ? $"Вне списка: {f.Processes.Count} · Недоступно: {f.ProcessUnavailable}. Не является списком вредоносных программ."
        : "Запущенные программы в быстрой проверке не проверялись.";
    public string EventStatus => Selected?.Full is { } f
        ? $"За 7 дней: критических {f.Events.Count(e => e.Level == 1)}, ошибок {f.Events.Count(e => e.Level == 2)}, предупреждений {f.Events.Count(e => e.Level == 3)}"
        : "События Windows в быстрой проверке не проверялись.";
    public string BenchmarkTitle => Selected?.Full?.Benchmark is { } b ? $"{b.Engine}: {b.Drive} · {b.MediaType} · SEQ, {b.Passes} прохода, 1 GiB" : "Тест скорости не выполнялся";
    public string BenchmarkRead => Selected?.Full?.Benchmark.ReadLabel ?? "—";
    public string BenchmarkWrite => Selected?.Full?.Benchmark.WriteLabel ?? "—";
    public string BenchmarkState => Selected?.Full?.Benchmark is not { } b ? "Нет результата" : b.State switch
    {
        "Completed" => b.MediaType is "SSD" or "HDD" ? $"Завершён. Ориентир чтения: {(b.IsNvme ? "NVMe 900" : "SSD 210")}, HDD 100 МБ/с. Запись без оценки." : "Завершён. Тип диска неизвестен, порог чтения не оценён.",
        "Running" => "Выполняется в фоне",
        "Skipped" => b.Error,
        _ => "Не выполнен: " + b.Error
    };
    public string Comparison
    {
        get
        {
            if (Selected is null) return "Нет данных для сравнения";
            var previous = History.FirstOrDefault(h => h.StartedAt < Selected.StartedAt && h.ComputerName == Selected.ComputerName && h.KindLabel == Selected.KindLabel);
            if (previous is null) return "Предыдущей проверки этого типа пока нет";
            var lines = new List<string> { $"Предыдущая: {previous.DateLabel}", $"Загрузка процессора: {previous.CpuLabel} → {Selected.CpuLabel}; занято памяти: {previous.MemoryLabel} → {Selected.MemoryLabel}. Эти цифры меняются во время работы компьютера." };
            foreach (var disk in Selected.Disks)
            {
                var match = previous.Disks.Where(d => d.TotalBytes == disk.TotalBytes &&
                    (disk.VolumeId.Length > 0
                        ? string.Equals(d.VolumeId, disk.VolumeId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(d.Name.TrimEnd('\\'), disk.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))).ToList();
                if (match.Count == 1) lines.Add($"{disk.Name} свободно: {match[0].FreeBytes / 1073741824.0:N1} → {disk.FreeBytes / 1073741824.0:N1} ГБ. Изменение между проверками, не результат очистки.");
            }
            if (previous.Full is { } old && Selected.Full is { } current)
            {
                foreach (var disk in current.SmartDisks)
                {
                    var match = old.SmartDisks.Where(d => d.Model == disk.Model).ToList();
                    if (match.Count == 1 && match[0].Status != disk.Status) lines.Add($"SMART {disk.Model}: {match[0].Status} → {disk.Status}");
                }
                var oldKeys = old.Events.Where(item => item.Level <= 2).Select(EventIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newErrors = current.Events.Where(item => item.Level <= 2 && !oldKeys.Contains(EventIdentity(item)))
                    .GroupBy(item => (item.Provider, item.Id, item.Level)).Select(group => group.First()).ToArray();
                lines.Add(newErrors.Length == 0
                    ? "Новых критических ошибок Windows в выборке нет."
                    : "Новые ошибки Windows: " + string.Join(", ", newErrors.Take(5).Select(item => $"{item.Provider} #{item.Id} ({item.LevelLabel.ToLowerInvariant()})"))
                        + (newErrors.Length > 5 ? $" и ещё {newErrors.Length - 5}" : ""));

                var oldProcesses = old.Processes.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newProcesses = current.Processes.Where(item => !oldProcesses.Contains(item.Name)).ToArray();
                if (newProcesses.Length > 0)
                    lines.Add("Новые процессы вне списка: " + string.Join(", ", newProcesses.Take(5).Select(DescribeProcess))
                        + (newProcesses.Length > 5 ? $" и ещё {newProcesses.Length - 5}" : "") + ". Это не означает, что программы вредоносные.");
                else lines.Add("Новых процессов вне списка нет.");

                var oldRead = old.Benchmark;
                var currentRead = current.Benchmark;
                if (oldRead.State == "Completed" && currentRead.State == "Completed" && oldRead.MediaType == currentRead.MediaType
                    && string.Equals(oldRead.Drive, currentRead.Drive, StringComparison.OrdinalIgnoreCase))
                {
                    if (oldRead.Read.HasValue && currentRead.Read.HasValue)
                        lines.Add($"Скорость чтения: {oldRead.Read:N0} → {currentRead.Read:N0} МБ/с.");
                    if (oldRead.Write.HasValue && currentRead.Write.HasValue)
                        lines.Add($"Скорость записи: {oldRead.Write:N0} → {currentRead.Write:N0} МБ/с.");
                }
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string EventIdentity(EventDetails item) => item.RecordId > 0
        ? $"{item.Log}|record:{item.RecordId}"
        : $"{item.Log}|{item.Provider}|{item.Id}|{item.Level}|{item.Message}";

    private static string DescribeProcess(ProcessDetails item)
    {
        var label = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : Path.GetFileNameWithoutExtension(item.Name);
        return string.IsNullOrWhiteSpace(item.Publisher) ? label : $"{label} ({item.Publisher})";
    }

    public Task RunFullAsync() => RunFullCoreAsync(false);
    public Task RunQuickFullAsync() => RunFullCoreAsync(false, true);
    public Task RunUserFullAsync() => RunFullCoreAsync(true);
    public Task RunScheduledUserQuickAsync() => RunFullCoreAsync(true, true);

    public void OpenLowSpaceScan()
    {
        if (busy || LowSpaceDisk is not { } disk) return;
        TreeSizeLauncher.StartScan(disk.Name);
        userStatusOverride = $"Открыт анализ диска {disk.Name.TrimEnd('\\')} без прав администратора.";
        Notify();
    }

    private async Task RunFullCoreAsync(bool userMode, bool quickMode = false)
    {
        if (busy || fullRunner is null) return;
        if (!quickMode) _ = CheckApplicationUpdatesAsync(automatic: true);
        if (!userMode)
        {
            CheckProgressEntries.Clear();
            checkProgressGeneration++;
            checkProgressActive = true;
            checkProgressPhase = "Диагностика";
            RecordProgress("Проверка", quickMode ? "Подготовка быстрой полной проверки" : "Подготовка полной проверки");
        }
        userStatusOverride = userMode ? "Подготовка проверки…" : null;
        lastFullError = null;
        currentFullStage = quickMode ? "Подготовка быстрой полной проверки…" : "Подготовка полной проверки…";
        debugPreferSelected = false;
        busy = true; status = currentFullStage; Notify();
        RefreshDebug();
        try
        {
            if (!userMode && !quickMode)
            {
                RecordProgress("Обслуживание", "Отключение автоматической установки обновлений Windows");
                try
                {
                    windowsUpdatePolicyStatus = await new WindowsUpdatePolicyRunner().DisableAsync();
                    RecordProgress("Обслуживание", windowsUpdatePolicyStatus);
                }
                catch (Exception ex)
                {
                    windowsUpdatePolicyStatus = "Не удалось отключить автообновления: " + ex.Message;
                    RecordProgress("Обслуживание", windowsUpdatePolicyStatus);
                }
                Notify();
            }
            if (userMode)
            {
                userStatusOverride = "Собираем показатели компьютера…";
                live = Selected;
                Notify();
            }
            var progress = new Progress<DiagnosticProgress>(update =>
            {
                status = update.Stage;
                currentFullStage = update.Stage;
                if (!userMode)
                {
                    if (CheckProgressEntries.LastOrDefault(e => e.Source == "Проверка")?.Message != update.Stage)
                        RecordProgress("Проверка", update.Stage);
                    foreach (var message in update.Messages ?? [])
                    {
                        RecordProgress("Диски", FormatDiskProgress(message));
                    }
                }
                if (userMode) userStatusOverride = update.Stage switch
                {
                    var stage when stage.Contains("30 секунд") => "Проверяем нагрузку и состояние диска…",
                    var stage when stage.Contains("диска") || stage.Contains("SMART") || stage.Contains("SEQ") => "Проверяем состояние и скорость диска…",
                    _ => "Проверка выполняется…"
                };
                if (update.Snapshot is not null)
                {
                    if (userMode) { live = update.Snapshot; Notify(); }
                    else Selected = update.Snapshot;
                }
                else RefreshDebug();
                Notify();
            });
            var snapshot = await Task.Run(() => userMode ? fullRunner.RunUserAsync(progress)
                : quickMode ? fullRunner.RunQuickAsync(progress) : fullRunner.RunAsync(progress));
            live = null;
            Selected = snapshot;
            await history.SaveAsync(snapshot);
            await ReloadAsync();
            status = quickMode ? "Быстрая полная проверка завершена. Результат сохранён" : "Полная проверка завершена. Результат сохранён";
            if (!userMode)
            {
                RecordProgress("Проверка", "Диагностика завершена; результат сохранён");
                if (snapshot.Full?.Benchmark is { State: "Failed" } benchmark) RecordProgress("Диски", "Ошибка теста: " + benchmark.Error);
                checkProgressPhase = "Очистка";
            }
            currentFullStage = null;
            RefreshDebug();
            userStatusOverride = userMode ? "Проверка завершена. Результат сохранён." : null;
            if (!userMode) _ = RunPostCheckMaintenanceAsync(checkProgressGeneration);
        }
        catch (Exception ex)
        {
            if (!userMode) { RecordProgress("Ошибка", ex.Message); checkProgressPhase = "Ошибка проверки"; checkProgressActive = false; }
            lastFullError = ex.Message;
            status = $"Полная проверка: {ex.Message}";
            if (userMode) userStatusOverride = "Проверку не удалось завершить. Подробности доступны инженеру.";
            RefreshDebug();
        }
        finally { live = null; busy = false; Notify(); }
    }

    public Task RunCleanupAsync() => RunCleanupCoreAsync(background: false);

    private async Task RunPostCheckMaintenanceAsync(int generation)
    {
        await Task.Yield();
        try
        {
            if (cleanupRunning)
            {
                RecordProgress("Очистка", "Очистка уже выполняется отдельно", generation);
                if (generation == checkProgressGeneration) checkProgressPhase = "Диагностика завершена";
                return;
            }
            await RunCleanupCoreAsync(background: true, generation);
            var hasErrors = (cleanupResult?.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase) ?? false)
                || (cleanupResult?.Contains("не очищены", StringComparison.OrdinalIgnoreCase) ?? false);
            RecordProgress("Готово", hasErrors ? "Проверка завершена; очистка требует внимания" : "Проверка и очистка завершены", generation);
            if (generation == checkProgressGeneration) checkProgressPhase = hasErrors ? "Завершено с замечаниями" : "Завершено";
        }
        catch (Exception ex) { RecordProgress("Ошибка", ex.Message, generation); if (generation == checkProgressGeneration) checkProgressPhase = "Завершено с ошибкой"; }
        finally { if (generation == checkProgressGeneration) { checkProgressActive = false; Notify(); } }
    }

    private async Task RunCleanupCoreAsync(bool background, int? generation = null)
    {
        if (cleanupRunning || (busy && !background)) return;
        if (!background) userStatusOverride = null;
        cleanupRunning = true;
        if (generation == checkProgressGeneration) { checkProgressPhase = "Очистка"; RecordProgress("Очистка", "Запуск очистки Windows", generation); }
        cleanupResult = "Очистка выполняется…";
        status = background ? "Отчёт готов. Очистка запущена отдельно…" : "Подготовка очистки Windows…";
        Notify();
        try
        {
            var progress = new Progress<string>(stage => { cleanupResult = stage; if (!background) status = stage; if (generation.HasValue) RecordProgress("Очистка", stage, generation); Notify(); });
            var result = await (cleanupRunner ?? new UserCleanupRunner()).RunAsync(progress);
            cleanupResult = result.Summary;
            if (generation.HasValue) RecordProgress("Очистка", result.Summary, generation);
            status = background ? "Отчёт готов. Очистка завершена." : result.Summary;
        }
        catch (Exception ex) { cleanupResult = "Ошибка очистки: " + ex.Message; status = cleanupResult; if (generation.HasValue) RecordProgress("Ошибка", cleanupResult, generation); }
        finally { cleanupRunning = false; Notify(); }
    }

    public async Task RunRepairAsync()
    {
        if (repairRunning || busy) return;
        repairRunning = true;
        repairResult = "Подготовка восстановления Windows…";
        status = repairResult;
        Notify();
        try
        {
            var progress = new Progress<string>(stage => { repairResult = stage; status = stage; Notify(); });
            repairResult = await new SystemRepairRunner().RunAsync(progress);
            status = repairResult;
        }
        catch (Exception ex) { repairResult = "Ошибка восстановления: " + ex.Message; status = repairResult; }
        finally { repairRunning = false; Notify(); }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source is not null) foreach (var value in source) target.Add(value);
    }

    public async Task InitializeAsync()
    {
        busy = true; Notify();
        try
        {
            RefreshTreeSizeVolumes();
            RefreshNetworkAdapters();
            RefreshIdentity();
            windowsUpdatePolicyStatus = new WindowsUpdatePolicyRunner().ReadStatus();
            await history.SaveManyAsync(await FullDiagnosticsRunner.ReadInstalledReportsAsync());
            await ReloadAsync();
            status = History.Count > 0 ? "Последняя проверка загружена" : "Готово к первой проверке";
        }
        catch (Exception ex) { status = $"Не удалось прочитать историю: {ex.Message}"; }
        finally { busy = false; Notify(); }
    }

    public async Task RefreshLiveStatusAsync()
    {
        if (busy || cleanupRunning || repairRunning || windowsUpdateActionRunning
            || Interlocked.Exchange(ref liveRefreshRunning, 1) != 0) return;
        try
        {
            if (DateTimeOffset.UtcNow >= nextTemperatureProbeUtc)
            {
                nextTemperatureProbeUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                await CpuTemperatureCache.RequestProbeAsync();
            }

            var snapshot = runner is WindowsDiagnosticsRunner windowsRunner
                ? await windowsRunner.RunLiveAsync()
                : await runner.RunAsync();
            var temperature = CpuTemperatureCache.ReadFresh();
            live = snapshot with
            {
                CpuTemperatureC = temperature?.TemperatureC ?? Selected?.CpuTemperatureC ?? Selected?.Full?.CpuTemperatureC,
                CpuTemperatureStatus = temperature?.Status ?? Selected?.CpuTemperatureStatus ?? snapshot.CpuTemperatureStatus
            };
            Notify();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (live is null && Selected is null) status = "Не удалось получить текущие показатели: " + ex.Message;
            Notify();
        }
        finally
        {
            Interlocked.Exchange(ref liveRefreshRunning, 0);
        }
    }

    public async Task RunQuickAsync()
    {
        if (busy) return;
        userStatusOverride = null;
        busy = true; status = "Проверяем загрузку процессора, память и свободное место…"; Notify();
        try
        {
            var snapshot = await runner.RunAsync();
            Selected = snapshot;
            status = "Сохранение результата…"; Notify();
            await history.SaveAsync(snapshot);
            await ReloadAsync();
            status = "Быстрая проверка завершена. Результат сохранён";
        }
        catch (Exception ex) { status = $"Ошибка проверки или сохранения: {ex.Message}"; }
        finally { busy = false; Notify(); }
    }

    private async Task ReloadAsync()
    {
        var rows = await history.GetRecentAsync();
        History.Clear();
        foreach (var row in rows) History.Add(row);
        Selected = History.FirstOrDefault();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
