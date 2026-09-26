using System.IO;
using System.Diagnostics;
using System.Text;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Text.Json;
using Microsoft.Win32;
using ITSeti.Maintenance.App;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--progress-regression") || args.Contains("--installed-user-check"))
            return CheckProgressRegression(args.Contains("--installed-user-check"));
        if (args.Contains("--update-client-contract")) return CheckUpdateClientContract().GetAwaiter().GetResult();
        if (args.Contains("--interactive-disk-tools")) return CheckInteractiveDiskTools();
        AssertEngineerAccountParsing();
        var fullFixture = Environment.GetEnvironmentVariable("ITSETI_FULL_FIXTURE");
        AssertCleanupSummaryFormatting();
        var output = Path.GetFullPath(Path.Combine("artifacts", "smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        var temperatureCachePath = Path.Combine(output, "temperature-cache.json");
        File.WriteAllText(temperatureCachePath, JsonSerializer.Serialize(new CpuTemperatureReading(62, "smoke", DateTimeOffset.UtcNow)));
        if (CpuTemperatureCache.ReadFresh(temperatureCachePath)?.TemperatureC != 62)
            throw new Exception("A fresh CPU temperature cache entry was rejected");
        File.WriteAllText(temperatureCachePath, JsonSerializer.Serialize(new CpuTemperatureReading(62, "smoke", DateTimeOffset.UtcNow.AddMinutes(-3))));
        if (CpuTemperatureCache.ReadFresh(temperatureCachePath) is not null)
            throw new Exception("A stale CPU temperature cache entry was accepted");
        File.WriteAllText(temperatureCachePath, JsonSerializer.Serialize(new CpuTemperatureReading(140, "smoke", DateTimeOffset.UtcNow)));
        if (CpuTemperatureCache.ReadFresh(temperatureCachePath) is not null)
            throw new Exception("An out-of-range CPU temperature cache entry was accepted");
        var chosenTemperature = CpuTemperatureReader.SelectBestReading([
            ("CPU Core #1 Distance to TjMax", 91.0),
            ("Core #1", 66.0),
            ("CPU Package", 68.0),
            ("CPU (Tctl/Tdie)", 72.0)]);
        if (chosenTemperature is not { Name: "CPU (Tctl/Tdie)", Value: 72.0 })
            throw new Exception("CPU temperature selection picked a derived limit or a lower-priority sensor");
        if (CpuTemperatureReader.SelectBestReading([("Core #1 Distance to TjMax", 54.0)]) is not null)
            throw new Exception("Distance-to-limit sensor was reported as CPU temperature");
        var cacheRacePath = Path.Combine(Path.GetTempPath(), "ITSeti-temperature-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(cacheRacePath, JsonSerializer.Serialize(new CpuTemperatureReading(62, "smoke", DateTimeOffset.UtcNow)));
        var cacheWriter = Task.Run(() =>
        {
            var temporary = cacheRacePath + ".next";
            for (var i = 0; i < 100; i++)
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new CpuTemperatureReading(60 + i % 10, "smoke", DateTimeOffset.UtcNow)));
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(temporary, cacheRacePath, true); break; }
                    catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                    { Thread.Sleep(20 * (attempt + 1)); }
                }
            }
        });
        var cacheReader = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                if (CpuTemperatureCache.ReadFresh(cacheRacePath) is null)
                    throw new Exception("Concurrent CPU temperature cache read failed during atomic replacement");
        });
        try { Task.WhenAll(cacheWriter, cacheReader).GetAwaiter().GetResult(); }
        finally
        {
            File.Delete(cacheRacePath);
            File.Delete(cacheRacePath + ".next");
        }
        var database = Path.Combine(output, "history.db");
        var app = new ITSeti.Maintenance.App.App();
        app.InitializeComponent();
        var window = new MainWindow(database);
        var code = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var userShell = (UIElement)window.FindName("UserShell")!;
                var adminShell = (UIElement)window.FindName("AdminShell")!;
                if (userShell.Visibility != Visibility.Visible || adminShell.Visibility != Visibility.Collapsed)
                    throw new Exception("User mode is not the first screen");
                if (((Grid)window.FindName("AdminUnlock")!).Visibility != Visibility.Collapsed)
                    throw new Exception("Engineer sign-in dialog must stay closed on the user start screen");
                if (window.ResizeMode != ResizeMode.CanMinimize || Find<ScrollViewer>(userShell) is null)
                    throw new Exception("User window must be minimizable and allow content scrolling");
                var userWorkArea = SystemParameters.WorkArea;
                var expectedUserHeight = Math.Min(Math.Max(700, userWorkArea.Height * 0.9), Math.Max(1, userWorkArea.Height - 24));
                if (window.Height < expectedUserHeight - 1 || window.Height > window.MaxHeight + 1)
                    throw new Exception("User window height must fill the preferred screen area without exceeding it");
                Capture(window, Path.Combine(output, "user-initial.png"));
                var support = new SupportDialog("0042", "123-456", "123456789") { Owner = window };
                support.Show();
                support.UpdateLayout();
                await Task.Delay(150);
                if (((TextBox)support.FindName("IdentityValue")!).Text != $"Инв. номер: 0042{Environment.NewLine}RMS: 123-456{Environment.NewLine}AnyDesk: 123456789"
                    || !((TextBox)support.FindName("IdentityValue")!).IsReadOnly
                    || !((TextBox)support.FindName("IdentityValue")!).IsReadOnlyCaretVisible
                    || !((TextBox)support.FindName("IdentityValue")!).AcceptsReturn
                    || !((Button)support.FindName("CopyAllButton")!).IsEnabled
                    || CountButtons((DependencyObject)support.FindName("IdentityPanel")!) != 1)
                    throw new Exception("Support dialog is missing selectable PC identifiers or quick-copy action");
                Capture(support, Path.Combine(output, "support.png"));
                support.Close();
                while (!window.ViewModel.CanRun) await Task.Delay(50);
                while (window.ViewModel.UserMemoryLabel == "—") await Task.Delay(50);
                if (window.ViewModel.UserDiskDetail == "Нет данных о диске")
                    throw new Exception("First app launch did not populate live disk capacity");
                if (window.ViewModel.Selected is null && window.ViewModel.UserCoverage.Length > 0)
                    throw new Exception("Live-only status incorrectly reports diagnostic coverage errors");
                if (window.ViewModel.Selected is null && window.ViewModel.UserIssueHeading != "Проверка ещё не выполнялась")
                    throw new Exception("The initial user screen must not imply that a diagnostic has completed");
                if (window.ViewModel.UserIssueHeading.Contains("не всё", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("The initial user screen must not claim that checks are incomplete");
                var initialHistoryCount = window.ViewModel.History.Count;
                var firstRun = window.ViewModel.RunQuickAsync();
                if (window.ViewModel.UserIssueHeading != "Проверка выполняется" || window.ViewModel.UserCoverage.Length > 0)
                    throw new Exception("The user screen must show an in-progress diagnostic state");
                await firstRun;
                var first = window.ViewModel.Selected ?? throw new Exception("No snapshot produced");
                if (first.TotalMemoryBytes == 0 || first.CpuPercent is < 0 or > 100 || first.Disks.Count == 0 || first.QuickDisks is not { Count: > 0 }
                    || first.LastBootAt is null || first.LastBootAt > first.StartedAt)
                    throw new Exception("Invalid system measurements");
                if (first.CpuTemperatureC is < 5 or > 120
                    || first.CpuTemperatureC is not null && first.CpuTemperatureStatus?.Contains("LibreHardwareMonitor", StringComparison.Ordinal) != true)
                    throw new Exception("CPU temperature source returned an invalid value or an unexpected provider");
                var identityPath = Path.Combine(output, "inventory.txt");
                var identityStore = new MachineIdentityStore(identityPath);
                identityStore.SaveInventoryNumber("0042");
                if (identityStore.Read().InventoryNumber != "0042" || MachineIdentityStore.IsValidInventoryNumber("42"))
                    throw new Exception("Inventory number validation or persistence failed");
                if (MachineIdentityStore.ParseRmsInternetId("\uFEFF<?xml version=\"1.0\"?><rms><internet_id>123-456</internet_id></rms>") != "123-456"
                    || MachineIdentityStore.ParseRmsInternetId("broken xml") is not null)
                    throw new Exception("RMS Internet ID parsing failed with UTF-8 BOM");
                if (MachineIdentityStore.ParseAnyDeskId("ad.anynet.id=123456789\r\nservice.conf=private") != "123456789"
                    || MachineIdentityStore.ParseAnyDeskId("ad.anynet.id=not-a-number") is not null)
                    throw new Exception("AnyDesk public ID parsing failed");
                using (var rmsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TektonIT\RMS Host\Host\Parameters"))
                    if (rmsKey?.GetValue("InternetId") is byte[] && new MachineIdentityStore().Read().RmsId is null)
                        throw new Exception("Installed RMS Internet ID was not detected");
                var oldBoot = first with { LastBootAt = first.StartedAt - TimeSpan.FromDays(7) };
                var rebootIssue = DiagnosticRules.GetUserIssues(oldBoot).FirstOrDefault(i => i.Title.Contains("перезагружали"));
                if (rebootIssue is null || !rebootIssue.Detail.Contains("Сохраните открытые документы"))
                    throw new Exception("Old-boot warning is missing");
                if (DiagnosticRules.GetUserIssues(first with { LastBootAt = first.StartedAt - TimeSpan.FromDays(6) })
                    .Any(i => i.Title.Contains("перезагружали"))) throw new Exception("Six-day reboot boundary is wrong");
                if (!window.ViewModel.UserDiskHealth.Contains("по данным Windows") || !window.ViewModel.Coverage.Contains("полной диагностике"))
                    throw new Exception("Quick disk health or user explanation missing");
                await window.ViewModel.RunQuickAsync();
                if (window.ViewModel.History.Count != initialHistoryCount + 2) throw new Exception(window.ViewModel.Status);
                var saved = await new SqliteHistoryStore(database).GetRecentAsync();
                if (saved.Count != initialHistoryCount + 2 || saved[1].Id != first.Id || saved[1].QuickDisks?.Count != first.QuickDisks.Count)
                    throw new Exception("SQLite history roundtrip failed");
                window.ViewModel.Selected = window.ViewModel.History[1];
                if (window.ViewModel.Selected.Id != first.Id) throw new Exception("History selection failed");
                CapturePublicUserHome(window, Path.Combine(output, "user-home.png"));
                var normalExtent = Find<ScrollViewer>(userShell)!.ExtentHeight;
                var stressed = first with
                {
                    CpuPercent = 95,
                    AvailableMemoryBytes = 500_000_000,
                    Disks = [new DiskSnapshot("C:\\", 100L * 1073741824, 5L * 1073741824),
                        new DiskSnapshot("D:\\", 100L * 1073741824, 9L * 1073741824)],
                    Full = new FullDiagnosticDetails("CPU", "GPU", false, [], [], [], [], 0, 0, false,
                        new DiskBenchmark("C:", "SSD", 300, 300, 2, "Completed", ""), "", [],
                        new ResourceSampleSummary(7, 7, 7, 0, 0, 95, 95, 0, null, null, 0, 0, ""))
                };
                var partial = first with
                {
                    CpuPercent = 10,
                    TotalMemoryBytes = 16UL * 1073741824,
                    AvailableMemoryBytes = 8UL * 1073741824,
                    Disks = [new DiskSnapshot("C:\\", 100L * 1073741824, 50L * 1073741824)],
                    LastBootAt = first.StartedAt - TimeSpan.FromDays(1),
                    Full = new FullDiagnosticDetails("Test CPU", "Test GPU", false, [], [], [], [], 0, 0, false,
                        new DiskBenchmark("C:", "Unknown", null, null, 2, "Skipped", ""), "", [])
                };
                window.ViewModel.Selected = partial;
                if (window.ViewModel.UserIssueHeading != "Есть ограничения проверки")
                    throw new Exception("The user screen must describe unavailable checks without claiming no diagnostic ran");
                window.ViewModel.Selected = stressed;
                window.UpdateLayout();
                await Task.Delay(250);
                if (!window.ViewModel.HasMoreUserIssues || Find<ScrollViewer>(userShell)!.ExtentHeight <= normalExtent
                    || ((Button)window.FindName("MoreIssuesButton")!).Visibility != Visibility.Visible
                    || !DiagnosticRules.GetUserIssues(stressed).Any(issue => issue.Title.Contains("мало места")))
                    throw new Exception("User window did not expand for multiple issues");
                Capture(window, Path.Combine(output, "user-alerts.png"));
                window.ViewModel.Selected = stressed with { Full = stressed.Full! with
                {
                    SmartDisks = [new SmartDiskDetails("Test SSD", "Good", "C:", "SSD", "SATA/600 | SATA/600")],
                    EventLimited = true, EventUnavailable = 1,
                    Events = [new EventDetails("System", "Test", 1, 1, "2026-09-23", "critical")]
                } };
                if (window.ViewModel.UserCoverage.Contains("событ", StringComparison.OrdinalIgnoreCase)
                    || window.ViewModel.UserIssues.Any(i => i.Title.Contains("Windows записала"))
                    || window.ViewModel.Selected.ResultLabel.Contains("неполные данные"))
                    throw new Exception("Event collection leaked into user-facing findings");
                window.ViewModel.Selected = first;
                Click((Button)window.FindName("AdminModeButton")!);
                var password = (PasswordBox)window.FindName("AdminPassword")!;
                window.UpdateLayout();
                await Task.Delay(150);
                if (((Grid)window.FindName("AdminUnlock")!).Visibility != Visibility.Visible)
                    throw new Exception("Engineer sign-in dialog did not open");
                var adminAccount = (ComboBox)window.FindName("AdminAccount")!;
                if (string.IsNullOrWhiteSpace(adminAccount.Text))
                    throw new Exception("Windows account field is missing from the engineer sign-in dialog");
                var discoveredAccount = adminAccount.Text;
                adminAccount.Text = "CONTOSO\\manual-user";
                await Task.Delay(200);
                if (adminAccount.Text != "CONTOSO\\manual-user")
                    throw new Exception("Account discovery overwrote the manually entered engineer account");
                adminAccount.Text = discoveredAccount;
                Capture(window, Path.Combine(output, "engineer-login.png"));
                var unlockButton = (Button)window.FindName("UnlockButton")!;
                var adminError = (TextBlock)window.FindName("AdminError")!;
                password.Password = "wrong";
                Click(unlockButton);
                while (!unlockButton.IsEnabled) await Task.Delay(25);
                if (adminError.Visibility != Visibility.Visible)
                    throw new Exception("Invalid Windows credentials did not produce an error");
                if (adminShell.Visibility != Visibility.Collapsed || !window.IsVisible)
                    throw new Exception("Wrong admin password accepted or closed the user window");
                password.Password = "it-seti";
                Click(unlockButton);
                while (!unlockButton.IsEnabled) await Task.Delay(25);
                if (adminError.Visibility != Visibility.Visible)
                    throw new Exception("Legacy misspelled password unexpectedly opened engineer mode");
                if (adminShell.Visibility != Visibility.Collapsed) throw new Exception("Old admin password accepted");
                password.Password = "itseti";
                Click(unlockButton);
                if (adminShell.Visibility != Visibility.Visible || userShell.Visibility != Visibility.Collapsed)
                    throw new Exception("Admin mode did not open");
                if (window.ViewModel.UserCpuName == "Модель процессора не определена")
                    throw new Exception("Processor model is missing below the usage graph");
                if (window.FindName("AdminRmsValue") is not TextBox { IsReadOnly: true, IsReadOnlyCaretVisible: true }
                    || window.FindName("AdminAnyDeskValue") is not TextBox { IsReadOnly: true, IsReadOnlyCaretVisible: true })
                    throw new Exception("Engineer overview is missing mouse-selectable RMS and AnyDesk IDs");
                window.ViewModel.CheckProgressEntries.Add(new("12:00:01", "Проверка", "Оборудование и нагрузка проверены"));
                window.ViewModel.CheckProgressEntries.Add(new("12:00:02", "Диски", "CrystalDiskInfo: SMART data received"));
                window.ViewModel.CheckProgressEntries.Add(new("12:00:03", "Диски", "DiskSpd: read, pass 1/2 - 420 MB/s"));
                window.ViewModel.CheckProgressEntries.Add(new("12:00:04", "Очистка", "Очистка системных файлов и обновлений Windows"));
                var progressTabs = (TabControl)window.FindName("AdminTabs")!;
                progressTabs.SelectedItem = window.FindName("ProgressTab");
                window.UpdateLayout();
                await Task.Delay(200);
                Capture(window, Path.Combine(output, "admin-progress.png"));
                progressTabs.SelectedIndex = 0;
                if (window.ViewModel.OverviewFindings.Contains("Без доступа к исполняемому файлу")
                    || window.ViewModel.OverviewFindings.Contains("Неполная выборка"))
                    throw new Exception("Technical collection details leaked into overview");
                var debugRun = Path.Combine(output, "debug-run");
                Directory.CreateDirectory(debugRun);
                File.WriteAllText(Path.Combine(debugRun, "stage.txt"), "Проверка диска");
                File.WriteAllText(Path.Combine(debugRun, "disk-worker.log"), "DiskSpd failed");
                var debugSnapshot = stressed with { Full = stressed.Full! with
                {
                    ReportDirectory = debugRun,
                    Benchmark = new DiskBenchmark("C:", "SSD", null, null, 2, "Failed", "DiskSpd: отказано в доступе"),
                    EventLimited = true, EventUnavailable = 1
                }, Notes = ["SMART: CrystalDiskInfo не запустился"] };
                window.ViewModel.Selected = debugSnapshot;
                if (!window.ViewModel.DebugSteps.Any(s => s.Name == "Скорость диска" && s.State == "Ошибка" && s.Detail.Contains("отказано"))
                    || !window.ViewModel.DebugSteps.Any(s => s.Name == "Состояние дисков (SMART)" && s.Detail.Contains("не запустился"))
                    || !window.ViewModel.DebugFiles.Any(f => f.Name == "disk-worker.log")
                    || window.ViewModel.DebugStage != "Проверка диска")
                    throw new Exception("Admin debug details are missing");
                var failedDebug = new DiagnosticDebugView();
                failedDebug.Refresh(debugSnapshot, "Не удалось запустить задачу Windows", "Запуск проверки", preferSelected: true);
                if (!failedDebug.Source.Contains("завершилась с ошибкой")
                    || failedDebug.Steps.SingleOrDefault(step => step.Name == "Запуск проверки")?.Detail != "Не удалось запустить задачу Windows"
                    || failedDebug.Steps.Any(step => step.Name == "Скорость диска") || failedDebug.DirectoryPath.Length != 0)
                    throw new Exception("Failed run is mixed with an older successful report");
                failedDebug.Refresh(debugSnapshot, preferSelected: true);
                if (failedDebug.DirectoryPath != debugRun || failedDebug.Steps.All(s => s.Name != "Скорость диска"))
                    throw new Exception("Selected report is hidden by the latest failure");
                var debugTabs = Find<TabControl>(adminShell) ?? throw new Exception("Admin tabs missing");
                debugTabs.SelectedIndex = 7;
                window.UpdateLayout();
                await Task.Delay(250);
                window.UpdateLayout();
                Capture(window, Path.Combine(output, "admin-debug.png"));
                debugTabs.SelectedIndex = 0;
                debugTabs.SelectedIndex = 8;
                window.UpdateLayout();
                await Task.Delay(200);
                window.UpdateLayout();
                Capture(window, Path.Combine(output, "admin-maintenance.png"));
                debugTabs.SelectedIndex = 0;
                window.ViewModel.Selected = stressed with { Full = stressed.Full! with
                {
                    Events = [new EventDetails("System", "A", 1, 1, "2026-09-23", "critical"),
                        new EventDetails("System", "B", 2, 2, "2026-09-23", "error"),
                        new EventDetails("System", "C", 3, 3, "2026-09-23", "warning")]
                } };
                if (window.ViewModel.FilteredEvents.Cast<EventDetails>().Count() != 1) throw new Exception("Default event filter is not critical-only");
                window.ViewModel.EventFilterLevel = 2;
                if (window.ViewModel.FilteredEvents.Cast<EventDetails>().Count() != 2) throw new Exception("Error event filter failed");
                window.ViewModel.EventFilterLevel = 3;
                if (window.ViewModel.FilteredEvents.Cast<EventDetails>().Count() != 3) throw new Exception("All-event filter failed");
                window.ViewModel.EventFilterLevel = 1;
                window.ViewModel.Selected = first;
                if (window.ViewModel.SetupComponents.Count != 4) throw new Exception("Organization software audit missing components");
                if (window.ViewModel.SetupComponents.Select(component => component.InstallKey).Distinct().Count() != 4)
                    throw new Exception("Per-component install actions are missing");
                if (window.ViewModel.SystemTools.Count != 8 || window.ViewModel.SystemTools.All(tool => tool.Key != "security"))
                    throw new Exception("System application shortcuts are incomplete");
                var treeStart = TreeSizeLauncher.CreateStartInfo(@"C:\Tools\TreeSizeFree.exe", @"C:\");
                if (treeStart.UseShellExecute || treeStart.Verb.Length > 0 || treeStart.ArgumentList.Single() != @"C:\")
                    throw new Exception("TreeSize must launch in the current user session without UAC");
                var diskToolStart = ElevatedProcessLauncher.CreateStartInfo(@"C:\Tools\DiskInfo64.exe", @"C:\Tools");
                if (diskToolStart.UseShellExecute || diskToolStart.Verb.Length > 0)
                    throw new Exception("Disk utilities must launch in the current user session without UAC");
                var detectedSetup = OrganizationSoftwareAudit.FindSource();
                if (File.Exists(@"F:\Service\ITSETI-Setup\system\Install.ps1")
                    && !string.Equals(detectedSetup, @"F:\Service\ITSETI-Setup", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Moved ITSETI-Setup folder was not discovered under Service");
                await AssertSetupSignatureCheck(output);
                foreach (var name in new[] { "InstallSetupButton", "LaunchDiskInfoButton", "LaunchDiskMarkButton", "LaunchTreeSizeButton" })
                    if (window.FindName(name) is not Button { IsEnabled: true }) throw new Exception($"ПО action is unavailable: {name}");
                AssertDiskToolResolution(output);
                typeof(MainViewModel).GetField("identity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(window.ViewModel, new MachineIdentity("0042", "123-456-789-012", "123456789", []));
                typeof(MainViewModel).GetMethod("Notify", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(window.ViewModel, null);
                window.ViewModel.AdminInventoryInput = "0042";
                ((TextBlock)window.FindName("AdminComputerNameLabel")!).Text = "DEMO-PC";
                window.UpdateLayout();
                await Task.Delay(250);
                Capture(window, Path.Combine(output, "admin-overview.png"));
                var setupTabs = Find<TabControl>(window) ?? throw new Exception("Tabs missing");
                setupTabs.SelectedIndex = 5;
                window.UpdateLayout();
                await Task.Delay(250);
                Capture(window, Path.Combine(output, "organization-software.png"));
                setupTabs.SelectedIndex = 0;
                window.Width = 900; window.Height = 620;
                window.UpdateLayout();
                await Task.Delay(250);
                Capture(window, Path.Combine(output, "compact.png"));
                var tabs = Find<TabControl>(window) ?? throw new Exception("Tabs missing");
                tabs.SelectedIndex = 6;
                window.UpdateLayout();
                await Task.Delay(250);
                Capture(window, Path.Combine(output, "history.png"));
                AssertRules();
                if (!string.IsNullOrWhiteSpace(fullFixture))
                {
                    var imported = JsonSerializer.Deserialize<DiagnosticSnapshot>(await File.ReadAllTextAsync(fullFixture))
                        ?? throw new Exception("Full payload could not be imported");
                    if (imported.Full is null || imported.Full.PhysicalDisks.Count == 0 || imported.Full.Benchmark.State != "Skipped")
                        throw new Exception("Full payload missing hardware or benchmark state");
                    window.ViewModel.Selected = imported;
                    var groupedVolumes = window.ViewModel.DiskGroups.SelectMany(group => group.Partitions).Select(disk => disk.Name).ToArray();
                    if (groupedVolumes.Length != imported.Disks.Count || groupedVolumes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != groupedVolumes.Length)
                        throw new Exception("Disk partitions are missing from or duplicated across the grouped disk view");
                    if (!window.ViewModel.Findings.Contains("HDD")) throw new Exception("HDD advisory missing from report");
                    window.Width = 1160; window.Height = 760;
                    window.UpdateLayout();
                    await Task.Delay(250);
                    tabs.SelectedIndex = 0;
                    window.UpdateLayout();
                    await Task.Delay(250);
                    Capture(window, Path.Combine(output, "full-overview.png"));
                    tabs.SelectedIndex = 1;
                    window.UpdateLayout();
                    await Task.Delay(250);
                    Capture(window, Path.Combine(output, "physical-disks.png"));
                    tabs.SelectedIndex = 4;
                    window.UpdateLayout();
                    await Task.Delay(250);
                    Capture(window, Path.Combine(output, "events.png"));
                    Click((Button)window.FindName("ExitAdminButton")!);
                    window.UpdateLayout();
                    await Task.Delay(250);
                    var restoredWorkArea = SystemParameters.WorkArea;
                    var expectedRestoredHeight = Math.Min(Math.Max(700, restoredWorkArea.Height * 0.9), Math.Max(1, restoredWorkArea.Height - 24));
                    if (window.ResizeMode != ResizeMode.CanMinimize || window.Width != Math.Min(940, restoredWorkArea.Width - 24) || window.SizeToContent != SizeToContent.Height
                        || window.Height < expectedRestoredHeight - 1 || window.Height > window.MaxHeight + 1)
                        throw new Exception("User window did not return to fixed content-sized mode");
                    Capture(window, Path.Combine(output, "user-full.png"));
                }
                var returnedToUser = adminShell.Visibility != Visibility.Visible;
                if (returnedToUser)
                {
                    Click((Button)window.FindName("AdminModeButton")!);
                    ((PasswordBox)window.FindName("AdminPassword")!).Password = "itseti";
                    Click((Button)window.FindName("UnlockButton")!);
                }
                var reviewDirectory = Path.Combine(output, "review-run");
                Directory.CreateDirectory(reviewDirectory);
                File.WriteAllText(Path.Combine(reviewDirectory, "stage.txt"), "Проверка завершена");
                File.WriteAllText(Path.Combine(reviewDirectory, "disk-worker.log"), "DiskSpd read pass 2/2: 812 MB/s");
                var reviewSnapshot = first with
                {
                    Id = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.Now,
                    CpuPercent = 24,
                    CpuTemperatureC = 67,
                    CpuTemperatureStatus = "LibreHardwareMonitor / PawnIO · CPU Package · 3/3 замера",
                    Full = new FullDiagnosticDetails(
                        "AMD Ryzen 7 5700X 8-Core Processor", "NVIDIA GeForce RTX", true,
                        [new PhysicalDiskDetails("Samsung SSD 980 PRO", "SSD", "Healthy")],
                        [new SmartDiskDetails("Samsung SSD 980 PRO", "Good", "C:", "SSD", "PCIe 4.0 x4 | PCIe 4.0 x4", 12400)],
                        [new ProcessDetails("example.exe", "Пример приложения", "Microsoft Corporation", "Подписан", @"C:\Program Files\Example\example.exe")],
                        [new EventDetails("System", "Disk", 7, 1, DateTimeOffset.Now.ToString("g"), "Критическая ошибка тестового события"),
                         new EventDetails("Application", "Example", 1001, 2, DateTimeOffset.Now.ToString("g"), "Ошибка тестового приложения")],
                        0, 0, false,
                        new DiskBenchmark("C:", "SSD", 812, 690, 2, "Completed", "", "DiskSpd", true),
                        reviewDirectory,
                        [new MemoryProcessDetails("Google Chrome", "chrome", 1_200_000_000, "Google LLC")],
                        new ResourceSampleSummary(30, 0, 0, 0, 0, 24, 48, 0, 0.3, 0.2, 100, 90, ""),
                        "DDR4", 67)
                };
                var previousSelection = window.ViewModel.Selected;
                window.ViewModel.Selected = reviewSnapshot;
                window.ViewModel.History.Insert(0, reviewSnapshot);
                window.Width = 1160;
                var workArea = SystemParameters.WorkArea;
                window.Height = Math.Min(Math.Max(900, workArea.Height * 0.92), workArea.Height - 24);
                var reviewTabs = (TabControl)window.FindName("AdminTabs")!;
                var reviewTabNames = new[] { "overview", "storage", "network", "processes", "events", "software", "history", "debug", "maintenance", "progress" };
                for (var index = 0; index < reviewTabNames.Length; index++)
                {
                    reviewTabs.SelectedIndex = index;
                    window.UpdateLayout();
                    await Task.Delay(100);
                    Capture(window, Path.Combine(output, "tab-" + reviewTabNames[index] + ".png"));
                }
                window.ViewModel.History.Remove(reviewSnapshot);
                window.ViewModel.Selected = previousSelection;
                if (returnedToUser) Click((Button)window.FindName("ExitAdminButton")!);
                var engineerWindow = new MainWindow(database, engineerOnly: true);
                var engineerLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                engineerWindow.Loaded += (_, _) => engineerLoaded.TrySetResult();
                engineerWindow.Show();
                await engineerLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var engineerReadyDeadline = DateTime.UtcNow.AddSeconds(10);
                while (((Grid)engineerWindow.FindName("AdminShell")!).Visibility != Visibility.Visible
                    && DateTime.UtcNow < engineerReadyDeadline) await Task.Delay(50);
                engineerWindow.UpdateLayout();
                await Task.Delay(200);
                if (!window.IsVisible
                    || ((Grid)engineerWindow.FindName("UserShell")!).Visibility != Visibility.Collapsed
                    || ((Grid)engineerWindow.FindName("AdminShell")!).Visibility != Visibility.Visible
                    || ((Button)engineerWindow.FindName("ExitAdminButton")!).Content?.ToString() != "Закрыть окно"
                    || (Find<TabControl>(engineerWindow)?.SelectedIndex ?? -1) < 0
                    || Math.Abs(engineerWindow.Height - Math.Min(Math.Max(900, SystemParameters.WorkArea.Height * 0.92), SystemParameters.WorkArea.Height - 24)) > 1
                    || engineerWindow.Height > engineerWindow.MaxHeight)
                    throw new Exception("Elevated engineer window did not remain separate from the user window");
                Capture(engineerWindow, Path.Combine(output, "engineer-window.png"));
                engineerWindow.Close();
                if (!window.IsVisible) throw new Exception("Closing engineer window closed the user window");
                await AssertCleanupContract(output);
                Console.WriteLine("PASS: real CPU/RAM/disks, SQLite roundtrip, history selection, desktop/compact rendering, all ten admin tabs captured.");
                Console.WriteLine(output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return code;
    }

    private static async Task<int> CheckUpdateClientContract()
    {
        static string Payload(string version, string digest, string? url = null) => $$"""
            {"version":"{{version}}","tag":"v{{version}}","assetName":"ITSeti-Maintenance-Setup.exe",
             "size":12345,"digest":"{{digest}}","downloadUrl":"{{url ?? $"https://github.com/izivinizi/IT-Seti_client/releases/download/v{version}/ITSeti-Maintenance-Setup.exe"}}"}
            """;
        static HttpClient For(string payload) => new(new FakeReleaseHandler(payload));

        const string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var newer = new GitHubReleaseClient(For(Payload("0.9.5", digest)), "https://unit.test/latest");
        var update = await newer.GetUpdateAsync(new Version(0, 8, 0));
        if (update?.Version != new Version(0, 9, 5) || update.Digest != digest || update.Size != 12345 ||
            update.DownloadUrl != "https://github.com/izivinizi/IT-Seti_client/releases/download/v0.9.5/ITSeti-Maintenance-Setup.exe")
            throw new Exception("Valid GitHub release manifest was not accepted");

        var current = new GitHubReleaseClient(For(Payload("0.9.5", digest)), "https://unit.test/latest");
        if (await current.GetUpdateAsync(new Version(0, 9, 5, 0)) is not null)
            throw new Exception("Current release was incorrectly offered as an update");

        var invalid = new GitHubReleaseClient(For(Payload("0.9.5", "sha256:invalid")), "https://unit.test/latest");
        try
        {
            await invalid.GetUpdateAsync(new Version(0, 8, 0));
            throw new Exception("Release without a valid digest was accepted");
        }
        catch (InvalidDataException) { }

        var untrustedUrl = new GitHubReleaseClient(For(Payload("0.9.5", digest, "https://attacker.invalid/setup.exe")), "https://unit.test/latest");
        try
        {
            await untrustedUrl.GetUpdateAsync(new Version(0, 8, 0));
            throw new Exception("Installer URL outside the trusted GitHub repository was accepted");
        }
        catch (InvalidDataException) { }

        Console.WriteLine("PASS: GitHub release manifest version, trusted URL and SHA-256 contract.");
        return 0;
    }

    private sealed class FakeReleaseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }

    private static int CheckInteractiveDiskTools()
    {
        var names = new[] { "DiskMark64", "DiskInfo64" };
        var previous = names.SelectMany(Process.GetProcessesByName).Select(p => { using (p) return p.Id; }).ToHashSet();
        var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        try
        {
            var runner = new FullDiagnosticsRunner(Path.GetFullPath("Tools"));
            foreach (var diskInfo in new[] { false, true })
            {
                var result = runner.LaunchInteractiveDiskToolAsync(diskInfo).GetAwaiter().GetResult();
                Console.WriteLine(result);
                var opened = result.Contains("окно открыто") || result.Contains("окно уже открыто");
                if (elevated && !opened)
                    throw new Exception(result);
                if (!elevated && !opened && !result.Contains("токен администратора")
                    && !result.Contains("Windows требует права администратора"))
                    throw new Exception(result);
            }
            if (!elevated) Console.WriteLine("PASS: standard-user launch made no UAC request; privileged GUI tools were refused clearly.");
            return 0;
        }
        finally
        {
            foreach (var process in names.SelectMany(Process.GetProcessesByName))
                using (process)
                    if (!previous.Contains(process.Id))
                    {
                        if (!process.CloseMainWindow() || !process.WaitForExit(2000)) process.Kill();
                    }
        }
    }

    private static void AssertDiskToolResolution(string output)
    {
        var root = Path.Combine(output, "tool-name-fixture");
        var diskMark = Path.Combine(root, "CrystalDiskMark9");
        var diskInfo = Path.Combine(root, "CrystalDiskInfo9_6_3_Portable");
        Directory.CreateDirectory(diskMark);
        Directory.CreateDirectory(diskInfo);
        var standard = Path.Combine(diskMark, "DiskMark64.exe");
        var updated = Path.Combine(diskMark, "DiskMark64A.exe");
        var arm = Path.Combine(diskMark, "DiskMarkA64.exe");
        var info = Path.Combine(diskInfo, "DiskInfo64.exe");
        File.WriteAllBytes(standard, []);
        File.WriteAllBytes(info, []);
        if (FullDiagnosticsRunner.ResolveInteractiveDiskTool(root, false) != standard
            || FullDiagnosticsRunner.ResolveInteractiveDiskTool(root, true) != info)
            throw new Exception("Standard disk utility executable names were not resolved");
        File.Delete(standard);
        File.WriteAllBytes(updated, []);
        if (FullDiagnosticsRunner.ResolveInteractiveDiskTool(root, false) != updated)
            throw new Exception("DiskMark64A.exe was not resolved");
        File.Delete(updated);
        File.WriteAllBytes(arm, []);
        if (FullDiagnosticsRunner.ResolveInteractiveDiskTool(root, false) != arm)
            throw new Exception("DiskMarkA64.exe was not resolved");
    }

    private static async Task AssertSetupSignatureCheck(string output)
    {
        var root = Path.Combine(output, "setup signature fixture");
        var packages = Path.Combine(root, "system", "packages");
        Directory.CreateDirectory(packages);
        await File.WriteAllTextAsync(Path.Combine(root, "system", "Install.ps1"), "# deliberately unaudited fixture");
        var names = new[] { "AnyDesk-installer.exe", "Host-IT-SETI.RMS.7.7.3.0v3.msi", "OCS-Agent-Installerv4.exe", "DesktopInfo3230.exe" };
        foreach (var package in names)
            await File.WriteAllTextAsync(Path.Combine(packages, package), "not a signed installer");
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        File.Copy(dotnet, Path.Combine(packages, names[0]), true);
        var problems = await OrganizationSetupRunner.CheckAsync(root);
        if (!problems.Any(problem => problem.Contains("изменён после ревизии", StringComparison.OrdinalIgnoreCase))
            || names.Any(name => !problems.Any(problem => problem.StartsWith(name + ": SHA-256", StringComparison.OrdinalIgnoreCase)))
            || problems.Any(problem => problem.Contains("проверка Windows недоступна", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Package preflight did not report the modified script and every unpinned package: " + string.Join(" | ", problems));
    }

    private static void AssertRules()
    {
        var benchmark = new DiskBenchmark("C:", "SSD", 209.9, 1, 2, "Completed", "");
        var full = new FullDiagnosticDetails("CPU", "GPU", true, [], [], [], [], 0, 0, false, benchmark, "C:\\test");
        var snapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.Now, "Test", 12, 16_000_000_000, 8_000_000_000, [], [], full);
        if (!snapshot.ResultLabel.Contains("неполные данные")) throw new Exception("Missing data not marked in history");
        var unhealthy = new PhysicalDiskDetails("Test disk", "SSD", "Warning");
        var quickHealth = snapshot with { Full = null, QuickDisks = [unhealthy] };
        if (!quickHealth.ResultLabel.Contains("Требует внимания")
            || !DiagnosticRules.GetUserIssues(quickHealth).Any(i => i.Severity == "Critical"))
            throw new Exception("Unhealthy disk lost from quick result or history label");
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = full with { PhysicalDisks = [unhealthy] } })
            .Any(i => i.Severity == "Critical" && i.Title.Contains("требует проверки")))
            throw new Exception("Windows disk health missing from full user result");
        if (DiagnosticRules.GetFindings(snapshot).Count != 1) throw new Exception("SSD read threshold failed");
        var kernelPowerNoise = new[]
        {
            new EventDetails("System", "Microsoft-Windows-Kernel-Power", 41, 1, "now", "BugcheckCode: 0", 101),
            new EventDetails("System", "Microsoft-Windows-Kernel-Power", 41, 1, "now", "BugcheckCode: 0", 102)
        };
        if (DiagnosticRules.GetActionableEvents(kernelPowerNoise).Count != 0
            || DiagnosticRules.GetFindings(snapshot with { Full = full with { Events = kernelPowerNoise.ToList() } })
                .Any(finding => finding.Contains("Критических событий", StringComparison.Ordinal)))
            throw new Exception("One or two Kernel-Power 41 events with BugcheckCode 0 must not count as errors");
        var frequentKernelPower = kernelPowerNoise.Append(kernelPowerNoise[0] with { RecordId = 103 }).ToArray();
        if (DiagnosticRules.GetActionableEvents(frequentKernelPower).Count != 3
            || !DiagnosticRules.GetFindings(snapshot with { Full = full with { Events = frequentKernelPower.ToList() } })
                .Any(finding => finding.Contains("Критических событий в доступной выборке: 3", StringComparison.Ordinal)))
            throw new Exception("Three Kernel-Power 41 events with BugcheckCode 0 must be reported");
        if (DiagnosticRules.GetActionableEvents([kernelPowerNoise[0] with { Message = "BugcheckCode: 1" }]).Count != 1)
            throw new Exception("A nonzero Kernel-Power bugcheck must remain actionable");
        var process = new ProcessDetails("AmneziaVPN.exe", "AmneziaVPN Service", "Amnezia", "Valid", @"C:\Apps\AmneziaVPN.exe");
        var withLogAndProcess = snapshot with { Full = full with
        {
            Events = [new EventDetails("System", "Service Control Manager", 7024, 2, "now", "verbose event text")],
            Processes = [process]
        } };
        if (withLogAndProcess.HistoryDetailLabel.Contains("Amnezia", StringComparison.OrdinalIgnoreCase)
            || withLogAndProcess.HistoryDetailLabel.Length > 260
            || !withLogAndProcess.HistoryDetailLabel.Contains("Service Control Manager #7024", StringComparison.Ordinal))
            throw new Exception("History must contain concise issue facts, not process lists or verbose event text");
        if (DiskLifetime.ExceedsWarning(60_000) || !DiskLifetime.ExceedsWarning(60_001)
            || !DiskLifetime.Format(60_000).Contains("6 г. 310 дн.", StringComparison.Ordinal))
            throw new Exception("Drive power-on hour threshold or years/days conversion failed");
        var longRunningDisk = new SmartDiskDetails("Aged SSD", "Good", "C:", "SSD", "SATA/600", 60_001);
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = full with { SmartDisks = [longRunningDisk] } })
                .Any(issue => issue.Severity == "Warning" && issue.Title.Contains("60 000"))
            || DiagnosticRules.GetUserIssues(snapshot with { Full = full with { SmartDisks = [longRunningDisk with { PowerOnHours = 60_000 }] } })
                .Any(issue => issue.Title.Contains("60 000")))
            throw new Exception("Drive power-on hours warning boundary failed");
        var nvme = snapshot with { Full = full with { Benchmark = benchmark with { Read = 899, IsNvme = true } } };
        if (!DiagnosticRules.GetFindings(nvme).Any(line => line.Contains("ниже 900"))) throw new Exception("NVMe read threshold failed");
        if (DiagnosticRules.GetFindings(nvme with { Full = nvme.Full! with { Benchmark = nvme.Full!.Benchmark with { Read = 900 } } }).Any(line => line.Contains("ниже 900")))
            throw new Exception("NVMe threshold incorrectly flagged 900 MB/s");
        var oldWindows = snapshot with { WindowsEdition = "Windows 10", WindowsRelease = "1803", WindowsBuild = 17134 };
        if (!DiagnosticRules.GetUserIssues(oldWindows).Any(issue => issue.Title == "Версия Windows устарела")) throw new Exception("Windows 1809 advisory missing");
        var currentWindows = oldWindows with { WindowsRelease = "1809", WindowsBuild = 17763 };
        if (DiagnosticRules.GetUserIssues(currentWindows).Any(issue => issue.Title == "Версия Windows устарела")) throw new Exception("Windows 1809 boundary incorrectly flagged");
        snapshot = snapshot with { Full = full with { Benchmark = benchmark with { Read = 210, Write = 0 } } };
        if (DiagnosticRules.GetFindings(snapshot).Count != 0) throw new Exception("Write speed wrongly classified");
        snapshot = snapshot with { Full = full with { Benchmark = benchmark with { MediaType = "HDD", Read = 99 } } };
        if (DiagnosticRules.GetFindings(snapshot).Count != 1) throw new Exception("HDD threshold failed");
        snapshot = snapshot with { Full = full with { Benchmark = benchmark with { MediaType = "HDD", Read = 100 }, PhysicalDisks = [new PhysicalDiskDetails("HDD", "HDD", "Healthy")] } };
        if (DiagnosticRules.GetFindings(snapshot).Count != 1) throw new Exception("HDD advisory missing");
        if (!DiagnosticRules.IsLinkLimited("SATA/300 | SATA/600") || DiagnosticRules.IsLinkLimited("SATA/600 | SATA/600"))
            throw new Exception("SATA protocol rule failed");
        var pressure = new ResourceSampleSummary(7, 0, 7, 0, 0, 12, 95, 0, null, null, 0, 0, "");
        var memory = snapshot with { AvailableMemoryBytes = 500_000_000,
            Full = full with { ResourceSampling = pressure,
                TopMemoryProcesses = [new MemoryProcessDetails("Google Chrome", "chrome", 3_000_000_000, "Google")] } };
        if (!DiagnosticRules.GetUserIssues(memory).Any(i => i.Detail.Contains("Google Chrome")))
            throw new Exception("Friendly top memory process missing from user issue");
        if (DiagnosticRules.GetUserIssues(memory with { Full = full }).Any(i => i.Title.Contains("памят", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Single memory sample classified as sustained pressure");
        var memory89 = snapshot with { AvailableMemoryBytes = (ulong)(snapshot.TotalMemoryBytes * 0.11),
            Full = full with { ResourceSampling = pressure with { MemoryHighSamples = 0, MemoryAverage = 89 } } };
        if (DiagnosticRules.GetUserIssues(memory89).Any(i => i.Title.Contains("памят", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("89% memory usage crossed the 90% threshold");
        if (!DiagnosticRules.GetUserIssues(memory89 with { Full = memory89.Full! with
            { ResourceSampling = pressure with { MemoryHighSamples = 5 } } }).Any(i => i.Title.Contains("Память почти постоянно занята")))
            throw new Exception("Sustained 90% memory pressure was not reported");
        var criticalMemory = memory with { Full = memory.Full! with
            { ResourceSampling = pressure with { LowAvailableSamples = 7, PagingHighSamples = 4, PagesOutputAverage = 120 } } };
        if (!DiagnosticRules.GetUserIssues(criticalMemory).Any(i => i.Title.Contains("не хватает памяти") && i.Severity == "Critical"))
            throw new Exception("Paging pressure was not critical");
        if (!DiagnosticRules.GetUserIssues(snapshot with { TotalMemoryBytes = 4L * 1073741824, Full = full })
            .Any(i => i.Title.Contains("Небольшой объём памяти") && i.Severity == "Warning")) throw new Exception("4 GB RAM advisory missing");
        if (DiagnosticRules.GetUserIssues(snapshot with { TotalMemoryBytes = 8L * 1073741824, Full = full })
            .Any(i => i.Title.Contains("памят", StringComparison.OrdinalIgnoreCase))) throw new Exception("8 GB RAM flagged without pressure");
        var cpu = snapshot with { CpuPercent = 99, Full = full with { ResourceSampling = pressure with { CpuHighSamples = 5 } } };
        if (!DiagnosticRules.GetUserIssues(cpu).Any(i => i.Title.Contains("Процессор"))) throw new Exception("Sustained CPU pressure missing");
        if (DiagnosticRules.GetUserIssues(cpu with { Full = full }).Any(i => i.Title.Contains("Процессор")))
            throw new Exception("One CPU sample flagged as sustained pressure");
        var warmCpu = snapshot with { Full = full with { CpuTemperatureC = 80.1 } };
        if (!DiagnosticRules.GetUserIssues(warmCpu).Any(issue => issue.Severity == "Warning" && issue.Title.Contains("температура"))
            || DiagnosticRules.GetUserIssues(snapshot with { Full = full with { CpuTemperatureC = 80 } }).Any(issue => issue.Title.Contains("температура")))
            throw new Exception("CPU temperature warning threshold failed");
        var criticalCpu = snapshot with { Full = full with { CpuTemperatureC = 90 } };
        if (!DiagnosticRules.GetUserIssues(criticalCpu).Any(issue => issue.Severity == "Critical" && issue.Title.Contains("температура"))
            || DiagnosticRules.GetUserIssues(snapshot with { Full = full with { CpuTemperatureC = 89.9 } }).Any(issue => issue.Severity == "Critical"))
            throw new Exception("CPU temperature critical threshold failed");
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = null, CpuTemperatureC = 90 })
            .Any(issue => issue.Severity == "Critical" && issue.Title.Contains("температура")))
            throw new Exception("CPU temperature from the independent sensor reader was ignored");
        var lowSpace = snapshot with { Disks = [new DiskSnapshot("C:\\", 100L * 1073741824, 14L * 1073741824)] };
        if (!DiagnosticRules.GetUserIssues(lowSpace).Any(i => i.Title.Contains("места") && i.Severity == "Warning")) throw new Exception("15 GB space warning missing");
        lowSpace = lowSpace with { Disks = [new DiskSnapshot("C:\\", 100L * 1073741824, 4L * 1073741824)] };
        if (!DiagnosticRules.GetUserIssues(lowSpace).Any(i => i.Title.Contains("места") && i.Severity == "Critical")) throw new Exception("5 GB space critical missing");
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = full with { Benchmark = benchmark with { Read = 179 } } })
            .Any(i => i.Title.Contains("медленно") && i.Severity == "Critical")) throw new Exception("SSD critical speed missing");
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = full with { Benchmark = benchmark with { MediaType = "HDD", Read = 79 } } })
            .Any(i => i.Title.Contains("медленно") && i.Severity == "Critical")) throw new Exception("HDD critical speed missing");
        var sata = new SmartDiskDetails("SSD", "Good", "C:", "SSD", "SATA/300 | SATA/600");
        if (!DiagnosticRules.GetUserIssues(snapshot with { Full = full with { SmartDisks = [sata] } })
            .Any(i => i.Title.Contains("подключён") && i.Severity == "Warning")) throw new Exception("SATA link warning missing");
        var hddLatency = snapshot with { Full = full with { Benchmark = benchmark with { MediaType = "HDD", Read = 120 },
            ResourceSampling = pressure with { MemoryHighSamples = 0, DiskReadLatencyMs = 120, DiskReadOperations = 120 } } };
        if (!DiagnosticRules.GetUserIssues(hddLatency).Any(i => i.Title.Contains("долго отвечает") && i.Severity == "Warning"))
            throw new Exception("Sustained HDD latency advisory missing");
        if (DiagnosticRules.GetUserIssues(hddLatency with { Full = hddLatency.Full! with
            { ResourceSampling = pressure with { MemoryHighSamples = 0, DiskReadLatencyMs = 200, DiskReadOperations = 2 } } })
            .Any(i => i.Title.Contains("долго отвечает"))) throw new Exception("Short HDD latency spike flagged");
        var missingTest = snapshot with { Full = full with { Benchmark = benchmark with { State = "Skipped", Error = "Нет доступа" } } };
        if (DiagnosticRules.GetUserIssues(missingTest).Any(i => i.Title.Contains("не измерена")))
            throw new Exception("Incomplete disk test presented as cause of slowness");
    }

    private static async Task AssertCleanupContract(string output)
    {
        var backend = Path.Combine(AppContext.BaseDirectory, "Backend");
        var accountScriptPath = Path.Combine(backend, "Inspect-AccountContext.ps1");
        if (!File.Exists(accountScriptPath)) throw new Exception("Account-context diagnostic script was not included in the app");
        var accountScript = await File.ReadAllTextAsync(accountScriptPath);
        foreach (var forbidden in new[] { "Get-LocalUser", "net user", "runas", "-Credential", "ConvertTo-SecureString" })
            if (accountScript.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Account-context script must not enumerate accounts or attempt credential use");
        var accountViewModel = new ITSeti.Maintenance.App.MainViewModel(new WindowsDiagnosticsRunner(),
            new SqliteHistoryStore(Path.Combine(output, "account-context.db")));
        if (string.IsNullOrWhiteSpace(accountViewModel.CurrentAccountSummary))
            throw new Exception("Current account and token context is missing from the admin view");
        var cleanup = await File.ReadAllTextAsync(Path.Combine(backend, "Cleanup.ps1"));
        foreach (var category in new[] { "Recycle Bin", "Temporary Files", "Thumbnail Cache", "Internet Cache Files", "D3D Shader Cache", "Delivery Optimization Files", "Update Cleanup", "Device Driver Packages" })
            if (!cleanup.Contains(category, StringComparison.Ordinal)) throw new Exception("Missing cleanup category: " + category);
        var allowedPath = Path.Combine(backend, "allowed-processes.json");
        if (!File.Exists(allowedPath)) throw new Exception("Process allowlist was not included in the app");
        using var allowlist = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(allowedPath));
        if (!allowlist.RootElement.TryGetProperty("allowedNames", out var names) || names.GetArrayLength() == 0)
            throw new Exception("Packaged process allowlist is empty or malformed");
        var summary = await File.ReadAllTextAsync(Path.Combine(backend, "Summary.ps1"));
        if (!summary.Contains("if($allowed -contains $p.ProcessName)", StringComparison.Ordinal))
            throw new Exception("The diagnostic process scan does not filter its packaged allowlist");
        var fullWorker = await File.ReadAllTextAsync(Path.Combine(backend, "FullCheckWorker.ps1"));
        var diskWorker = await File.ReadAllTextAsync(Path.Combine(backend, "HeadlessDiskWorker.ps1"));
        var installedCheck = await File.ReadAllTextAsync(Path.Combine(backend, "InstalledCheck.ps1"));
        if (!fullWorker.Contains("SkipDiskBenchmark", StringComparison.Ordinal) ||
            !diskWorker.Contains("SkipBenchmark", StringComparison.Ordinal) ||
            !installedCheck.Contains("-SkipDiskBenchmark:$Quick", StringComparison.Ordinal))
            throw new Exception("Quick full check does not skip the sampler and disk benchmark while retaining SMART");
        await AssertUserCleanupWorker(output, backend, "interactive", "TEST OK");
        await AssertUserCleanupWorker(output, backend, "abort", "Очистка не запускалась");
        var model = new ITSeti.Maintenance.App.MainViewModel(new WindowsDiagnosticsRunner(),
            new SqliteHistoryStore(Path.Combine(output, "disk-groups.db")));
        var full = new FullDiagnosticDetails("Test CPU", "Test GPU", false,
            [new PhysicalDiskDetails("Samsung SSD", "SSD", "Healthy")],
            [new SmartDiskDetails("Samsung SSD", "Good", "C:", "SSD", "SATA/600", 60_001)],
            [], [], 0, 0, false,
            new DiskBenchmark("C:", "SSD", 250, 0, 2, "Completed", ""), output);
        var snapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.Now, "TEST", 10,
            8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024,
            [new DiskSnapshot("C:\\", 100L * 1024 * 1024 * 1024, 40L * 1024 * 1024 * 1024)], [], full);
        model.Selected = snapshot;
        if (model.DiskGroups.Count != 1 || model.DiskGroups[0].Partitions.Count != 1 ||
            model.DiskGroups[0].TransferMode != "SATA/600" || !model.DiskGroups[0].LifetimeWarning.Contains("60 000"))
            throw new Exception("Disk model, health, link mode and volume were not grouped together");
        if (!model.SystemDiskSummary.Contains("C:", StringComparison.OrdinalIgnoreCase)
            || !model.SystemDiskSummary.Contains("Samsung SSD", StringComparison.OrdinalIgnoreCase)
            || !model.SystemDiskSummary.Contains("наработ", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Compact system disk summary omitted model or lifetime");
        var comparisonProcess = new ProcessDetails("AmneziaVPN.exe", "AmneziaVPN Service", "Amnezia", "Valid", @"C:\Apps\AmneziaVPN.exe");
        var previous = snapshot with { StartedAt = DateTimeOffset.Now.AddMinutes(-2), Full = full with
        {
            Processes = [new ProcessDetails("explorer.exe", "Windows Explorer", "Microsoft", "Valid", @"C:\Windows\explorer.exe")],
            Benchmark = full.Benchmark with { Read = 245, Write = 180 }
        } };
        var current = snapshot with { StartedAt = DateTimeOffset.Now, Full = full with
        {
            Processes = [previous.Full!.Processes[0], comparisonProcess],
            Events = [new EventDetails("System", "Service Control Manager", 7024, 2, "now", "message")],
            Benchmark = full.Benchmark with { Read = 250, Write = 185 }
        } };
        model.History.Add(previous);
        model.History.Add(current);
        model.Selected = current;
        var comparisonLines = model.Comparison.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        if (comparisonLines[^1] != "Новые процессы вне списка: AmneziaVPN Service (Amnezia)"
            || model.Comparison.Contains("Эти цифры меняются", StringComparison.Ordinal))
            throw new Exception("Comparison must be concise and place newly observed processes last");
        if (!((TabControl)System.Windows.Application.Current.Windows.OfType<MainWindow>().First().FindName("AdminTabs")!).Items
                .OfType<TabItem>().Any(item => Equals(item.Header, "Сеть")))
            throw new Exception("Network tab is missing");
    }

    private static async Task AssertUserCleanupWorker(string output, string backend, string signal, string expected)
    {
        var directory = Path.Combine(output, "cleanup-" + signal);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "go.txt"), signal);
        var script = Path.Combine(backend, "UserCleanupWorker.ps1");
        var command = $"$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(directory))}')); "
            + $"$s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(script))}')); "
            + "& ([scriptblock]::Create([IO.File]::ReadAllText($s,[Text.Encoding]::UTF8))) -JobRoot $r -TestOnly";
        using var worker = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }
        }) ?? throw new Exception("Cleanup test worker did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.WaitForExitAsync(timeout.Token);
        var status = await File.ReadAllTextAsync(Path.Combine(directory, "user-status.txt"));
        if (worker.ExitCode != 0 || !status.Contains(expected)) throw new Exception("Cleanup test worker failed: " + status);
    }

    private static int CheckProgressRegression(bool runInstalledCheck)
    {
        var app = new ITSeti.Maintenance.App.App();
        app.InitializeComponent();
        var directory = Path.Combine(Path.GetTempPath(), "ITSeti-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var window = new MainWindow(Path.Combine(directory, "history.db"));
        var exitCode = 0;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            exitCode = 1;
            app.Shutdown(1);
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                while (!window.ViewModel.CanRun) await Task.Delay(50);
                Click((Button)window.FindName("AdminModeButton")!);
                ((PasswordBox)window.FindName("AdminPassword")!).Password = "itseti";
                Click((Button)window.FindName("UnlockButton")!);
                var tabs = (TabControl)window.FindName("AdminTabs")!;
                var entries = window.ViewModel.CheckProgressEntries;
                var list = (ListBox)window.FindName("CheckProgressList")!;
                for (var batch = 0; batch < 30; batch++)
                {
                    tabs.SelectedItem = window.FindName("ProgressTab");
                    window.UpdateLayout();
                    entries.Clear();
                    for (var i = 0; i < 30; i++)
                    {
                        entries.Add(new("12:00:00", "Диски", "CrystalDiskInfo: получение SMART " + i));
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    if (list.Items.Count != entries.Count) throw new Exception("Progress items are out of sync");
                    if (list.ItemContainerGenerator.ContainerFromItem(entries[^1]) is null)
                        throw new Exception("Latest progress entry is not visible");
                    tabs.SelectedIndex = 0;
                }
                Console.WriteLine("PASS: 900 progress updates, resets, tab switches and autoscroll; " + Environment.UserName);
                if (runInstalledCheck)
                {
                    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                        throw new Exception("This scenario must run without administrator privileges");
                    if (!FullDiagnosticsRunner.IsInstalled) throw new Exception("Installed backend is missing");
                    await window.ViewModel.RunUserFullAsync();
                    var full = window.ViewModel.Selected?.Full ?? throw new Exception(window.ViewModel.Status);
                    if (full.SmartDisks.Count == 0 || full.Benchmark.State != "Completed")
                        throw new Exception("Incomplete installed check: " + full.Benchmark.Error + " SMART=" + full.SmartDisks.Count);
                    Console.WriteLine($"PASS: {identity.Name}, non-admin; SMART={full.SmartDisks.Count}; " +
                        $"read={full.Benchmark.Read} MB/s; report={full.ReportDirectory}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(exitCode); }
        };
        app.Run(window);
        return exitCode;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertCleanupSummaryFormatting()
    {
        var formatter = typeof(MainViewModel).GetMethod("BriefCleanupStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new Exception("Cleanup summary formatter is missing");
        string Format(string value) => (string)(formatter.Invoke(null, [value]) ?? "");
        if (Format("Пользователь: Завершена; удалено 0 файлов | Система: Код 0; изменение свободного места 0,02 ГБ") != "Очистка выполнена")
            throw new Exception("Successful cleanup summary includes implementation details");
        if (Format("Пользователь: Завершена | Система: Файлы обновлений не очищены: доступа нет") != "Очистка профиля выполнена; системная очистка не выполнена")
            throw new Exception("Partial cleanup summary does not explain the missing system cleanup");
    }

    private static void AssertEngineerAccountParsing()
    {
        var local = EngineerWindowLauncher.ParseAccountCandidates(".\\  tech ");
        if (local.Count != 1 || local[0].UserName != "tech"
            || !string.Equals(local[0].Domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Dot-prefixed local account was not normalized for manual engineer login");

        var domain = EngineerWindowLauncher.ParseAccountCandidates("CONTOSO\\tech");
        if (domain.Count != 1 || domain[0] != ("tech", "CONTOSO"))
            throw new Exception("Manually entered domain account was parsed incorrectly");

        var upn = EngineerWindowLauncher.ParseAccountCandidates("tech@example.test");
        if (upn.Count != 1 || upn[0] != ("tech@example.test", string.Empty))
            throw new Exception("UPN account was parsed incorrectly");

        var shortName = EngineerWindowLauncher.ParseAccountCandidates("tech");
        if (shortName.Count == 0 || shortName[0].Domain != Environment.MachineName || shortName.Any(item => item.UserName != "tech"))
            throw new Exception("Short account name was not prepared for local/domain lookup");
    }

    private static int CountButtons(DependencyObject parent)
    {
        var count = parent is Button ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            count += CountButtons(VisualTreeHelper.GetChild(parent, i));
        return count;
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CapturePublicUserHome(MainWindow window, string path)
    {
        var replacements = new Dictionary<string, string>
        {
            ["ComputerNameLabel"] = "PC-TEST",
            ["InventoryLabel"] = "Инв. № 0042",
            ["RmsLabel"] = "RMS: 123-456",
            ["AnyDeskLabel"] = "AnyDesk: 123456789",
            ["IpLabel"] = "IP: 192.168.1.50"
        };
        var labels = replacements.Select(pair => ((TextBlock)window.FindName(pair.Key)!, pair.Value)).ToArray();
        foreach (var (label, text) in labels) label.SetCurrentValue(TextBlock.TextProperty, text);
        window.UpdateLayout();
        Capture(window, path);
        foreach (var (label, _) in labels)
            BindingOperations.GetBindingExpression(label, TextBlock.TextProperty)?.UpdateTarget();
    }
}
