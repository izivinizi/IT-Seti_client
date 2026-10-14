using System.IO;
using System.Windows;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly FullDiagnosticsRunner fullRunner = new();
    private readonly TaskCompletionSource initializationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string historyDatabasePath;
    private bool engineerOnly;
    private bool syncingAdminPassword;
    private DispatcherOperation? progressScroll;
    private bool closed;
    private bool refreshingSoftware;
    private DateTime catalogWriteTime;
    private readonly DispatcherTimer liveMetricsTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    public MainWindow() : this(DefaultHistoryDatabasePath(), false) { }

    public MainWindow(string databasePath) : this(databasePath, false) { }

    public MainWindow(string databasePath, bool engineerOnly, string? cleanupUserSid = null, string? cleanupUserLocalData = null)
    {
        InitializeComponent();
        historyDatabasePath = Path.GetFullPath(databasePath);
        this.engineerOnly = engineerOnly;
        if (!engineerOnly) ConfigureUserShellSize();
        ViewModel = new MainViewModel(new WindowsDiagnosticsRunner(), new SqliteHistoryStore(historyDatabasePath),
            fullRunner, engineerOnly ? new UserCleanupRunner(cleanupUserSid, cleanupUserLocalData) : null);
        ViewModel.SetWindowsDefenderAccess(false);
        DataContext = ViewModel;
        if (engineerOnly) ConfigureEngineerShell();
        ViewModel.CheckProgressEntries.CollectionChanged += (_, _) => QueueProgressScroll();
        CheckProgressList.IsVisibleChanged += (_, _) => QueueProgressScroll();
        liveMetricsTimer.Tick += async (_, _) =>
        {
            try
            {
                await ViewModel.RefreshLiveStatusAsync();
                ViewModel.RefreshMaintenanceStatus();
                var changed = File.GetLastWriteTimeUtc(ServerSoftwareCatalog.CachePath);
                if (changed != catalogWriteTime) { catalogWriteTime = changed; ViewModel.RefreshSetupAudit(); }
            }
            catch (Exception ex)
            {
                ApplicationErrorStore.Record("Интерфейс", "Обновление состояния главного окна", ex);
            }
        };
        Closed += (_, _) => { closed = true; liveMetricsTimer.Stop(); progressScroll?.Abort(); MaintenanceServiceClient.ClearEngineerAuthorization(); };
        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            await ViewModel.RefreshLiveStatusAsync();
            if (closed) return;
            _ = TryAutomaticServerEnrollmentAsync();
            liveMetricsTimer.Start();
            if (this.engineerOnly)
            {
                ViewModel.RefreshSetupAudit();
                ViewModel.RefreshIdentity();
                await ViewModel.RefreshAdminAccountAuditAsync();
            }
            _ = ViewModel.RefreshWindowsDefenderStatusAsync();
            initializationCompleted.TrySetResult();
            _ = RefreshSoftwareCatalogAsync();
        };
    }

    private async Task TryAutomaticServerEnrollmentAsync()
    {
        try
        {
            using var enrollment = new ServerEnrollmentClient();
            if (await enrollment.TryAutomaticEnrollmentAsync()) ViewModel.RefreshIdentity();
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Регистрация сервера", "Автоматическая привязка компьютера", ex);
        }
    }

    public async Task RunScheduledQuickCheckAsync()
    {
        await initializationCompleted.Task;
        await ViewModel.RunScheduledUserCheckAsync();
    }

    private void QueueProgressScroll()
    {
        if (closed || progressScroll?.Status == DispatcherOperationStatus.Pending) return;
        // CollectionChanged can arrive before WPF's collection view processes the new item.
        // Defer layout until all collection subscribers and data bindings have caught up.
        progressScroll = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!closed && CheckProgressList.IsVisible && CheckProgressList.Items.Count > 0)
                CheckProgressList.ScrollIntoView(CheckProgressList.Items[^1]);
        }));
    }

    private async void RunFull_Click(object sender, RoutedEventArgs e)
    {
        await TryAutomaticServerEnrollmentAsync();
        AdminTabs.SelectedItem = ProgressTab;
        await ViewModel.RunFullAsync();
    }
    private async void RunUserFull_Click(object sender, RoutedEventArgs e)
    {
        await TryAutomaticServerEnrollmentAsync();
        await ViewModel.RunUserFullAsync();
    }
    private void StopFullCheck_Click(object sender, RoutedEventArgs e) => ViewModel.StopFullCheck();
    private async void RunCleanup_Click(object sender, RoutedEventArgs e) => await ViewModel.RunCleanupAsync();
    private async void RunRepair_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Запустить DISM и SFC? Проверка может занять длительное время и не перезагрузит компьютер автоматически.", "Восстановление Windows", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ViewModel.RunRepairAsync();
    }
    private async void RefreshMaintenance_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshMaintenanceStatus();
        await ViewModel.RefreshWindowsDefenderStatusAsync();
    }
    private async void CheckWindowsUpdates_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckWindowsUpdatesAsync();
    private async void ToggleWindowsDefender_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WindowsDefenderEnabled == true &&
            MessageBox.Show(this,
                "Отключить защиту Microsoft Defender в реальном времени? Это снизит защиту компьютера. Политики организации и Tamper Protection приложение не обходит.",
                "Отключение защиты", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ViewModel.ToggleWindowsDefenderAsync();
    }
    private async void DisableWindowsUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Отключить автоматическую установку обновлений Windows? Проверять и устанавливать их нужно будет вручную.", "Отключение автообновлений", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await ViewModel.DisableWindowsAutomaticUpdatesAsync();
    }
    private async void RestoreWindowsUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Вернуть сохранённую до отключения настройку автообновлений?", "Восстановление настройки", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ViewModel.RestoreWindowsAutomaticUpdatesAsync();
    }
    private async void MinimizeDiagnosticData_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Установить минимальный доступный для этой редакции Windows уровень диагностических данных? На Pro обязательные данные останутся включены.",
                "Диагностические данные Windows", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ViewModel.MinimizeWindowsDiagnosticDataAsync();
    }
    private async void RestoreDiagnosticData_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RestoreWindowsDiagnosticDataAsync();
    private void ShowCleanupDetails_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, ViewModel.CleanupStatusDetails, "Результат очистки", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowRepairDetails_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, ViewModel.RepairStatusDetails, "Результат восстановления", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OpenRepairLog_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.RepairLogPath;
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { path } }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть журнал", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowSupport_Click(object sender, RoutedEventArgs e)
    {
        new SupportDialog(ViewModel.InventoryNumber, ViewModel.RmsId, ViewModel.AnyDeskId) { Owner = this }.ShowDialog();
    }
    private MyTicketsDialog? ticketsDialog;
    private void OpenMyTickets()
    {
        if (ticketsDialog is not null) { ticketsDialog.Activate(); return; }
        ticketsDialog = new MyTicketsDialog { Owner = this };
        try { ticketsDialog.ShowDialog(); }
        finally { ticketsDialog = null; }
    }
    private void ShowMyTickets_Click(object sender, RoutedEventArgs e) => OpenMyTickets();
    private void ShowContacts_Click(object sender, RoutedEventArgs e) => new ContactsDialog(
        $"Инв. номер: {ViewModel.InventoryNumber}\nRMS: {ViewModel.RmsId}\nAnyDesk: {ViewModel.AnyDeskId}") { Owner = this }.ShowDialog();
    private void CopyInventory_Click(object sender, RoutedEventArgs e) => CopyIdentity(ViewModel.InventoryNumber, "инвентарный номер");
    private void CopyRms_Click(object sender, RoutedEventArgs e) => CopyIdentity(ViewModel.RmsId, "номер RMS");
    private void CopyAnyDesk_Click(object sender, RoutedEventArgs e) => CopyIdentity(ViewModel.AnyDeskId, "номер AnyDesk");
    private void CopyIdentity(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { ClipboardHelper.Copy(value); }
        catch (Exception ex) { MessageBox.Show(this, $"Не удалось скопировать {label}: {ex.Message}", "Копирование", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenLowSpaceScan_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.OpenLowSpaceScanAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть анализ диска", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowAllIssues_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Owner = this, Title = "ИТ-Сети | Возможные причины", Icon = Icon,
            Width = 560, MinWidth = 560, MaxHeight = SystemParameters.WorkArea.Height * 0.85,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(243, 246, 248)),
            FontFamily = FontFamily
        };
        var list = new StackPanel();
        list.Children.Add(new TextBlock
        {
            Text = $"Возможные причины ({ViewModel.UserIssues.Count})", FontSize = 20,
            FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(20, 60, 135)),
            Margin = new Thickness(0, 0, 0, 16)
        });
        foreach (var issue in ViewModel.UserIssues)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            item.Children.Add(new TextBlock { Text = issue.Title, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(32, 58, 80)) });
            item.Children.Add(new TextBlock { Text = issue.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(79, 112, 133)) });
            list.Children.Add(item);
        }
        dialog.Content = new ScrollViewer
        {
            Content = list, Padding = new Thickness(26), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        dialog.ShowDialog();
    }
    private void ShowAdminPrompt_Click(object sender, RoutedEventArgs e)
    {
        AdminError.Visibility = Visibility.Collapsed;
        ClearAdminPassword();
        AdminUnlock.Visibility = Visibility.Visible;
        AdminPassword.Focus();
    }

    private void CancelAdmin_Click(object sender, RoutedEventArgs e)
    {
        ClearAdminPassword();
        AdminUnlock.Visibility = Visibility.Collapsed;
    }

    private void AdminPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (syncingAdminPassword) return;
        syncingAdminPassword = true;
        AdminVisiblePassword.Text = AdminPassword.Password;
        syncingAdminPassword = false;
    }

    private void AdminVisiblePassword_Changed(object sender, TextChangedEventArgs e)
    {
        if (syncingAdminPassword) return;
        syncingAdminPassword = true;
        AdminPassword.Password = AdminVisiblePassword.Text;
        syncingAdminPassword = false;
    }

    private void ToggleAdminPassword_Click(object sender, RoutedEventArgs e)
    {
        var show = AdminPassword.Visibility == Visibility.Visible;
        syncingAdminPassword = true;
        if (show)
        {
            AdminVisiblePassword.Text = AdminPassword.Password;
            AdminPassword.Visibility = Visibility.Collapsed;
            AdminVisiblePassword.Visibility = Visibility.Visible;
            AdminPasswordVisibilityIcon.Text = "\uE891";
            AdminPasswordVisibilityIcon.ToolTip = "Скрыть пароль";
            AdminVisiblePassword.Focus();
            AdminVisiblePassword.CaretIndex = AdminVisiblePassword.Text.Length;
        }
        else
        {
            AdminPassword.Password = AdminVisiblePassword.Text;
            AdminVisiblePassword.Visibility = Visibility.Collapsed;
            AdminPassword.Visibility = Visibility.Visible;
            AdminPasswordVisibilityIcon.Text = "\uE890";
            AdminPasswordVisibilityIcon.ToolTip = "Показать пароль";
            AdminPassword.Focus();
        }
        syncingAdminPassword = false;
        UpdateCapsLockStatus();
    }

    private void AdminPassword_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.CapsLock) Dispatcher.BeginInvoke(UpdateCapsLockStatus);
        else UpdateCapsLockStatus();
    }

    private void UpdateCapsLockStatus() => AdminCapsLockStatus.Visibility = Keyboard.IsKeyToggled(Key.CapsLock)
        ? Visibility.Visible : Visibility.Collapsed;

    private void ClearAdminPassword()
    {
        syncingAdminPassword = true;
        AdminPassword.Clear();
        AdminVisiblePassword.Clear();
        AdminPassword.Visibility = Visibility.Visible;
        AdminVisiblePassword.Visibility = Visibility.Collapsed;
        AdminPasswordVisibilityIcon.Text = "\uE890";
        AdminPasswordVisibilityIcon.ToolTip = "Показать пароль";
        syncingAdminPassword = false;
        UpdateCapsLockStatus();
    }

    private async void AdminPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await UnlockAdminAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelAdmin_Click(sender, e); e.Handled = true; }
    }
    private async void UnlockAdmin_Click(object sender, RoutedEventArgs e) => await UnlockAdminAsync();

    private async Task UnlockAdminAsync()
    {
        var password = AdminVisiblePassword.Visibility == Visibility.Visible
            ? AdminVisiblePassword.Text
            : AdminPassword.Password;
        ClearAdminPassword();
        var result = await MaintenanceServiceClient.AuthenticateEngineerAsync(password);
        if (!result.Connected && EngineerOfflineAuthentication.Verify(password))
        {
            ShowEngineerShell();
            MessageBox.Show("Служба обслуживания не отвечает. Инженерное меню открыто; для системных действий восстановите службу.",
                "Инженерное меню", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!result.Accepted)
        {
            AdminError.Text = result.Message;
            AdminError.Visibility = Visibility.Visible;
            AdminPassword.Focus();
            return;
        }
        ShowEngineerShell();
    }

    private void ShowEngineerShell()
    {
        ConfigureEngineerShell();
        ViewModel.SetWindowsDefenderAccess(true);
        ViewModel.RefreshSetupAudit();
        ViewModel.RefreshIdentity();
        _ = ViewModel.RefreshAdminAccountAuditAsync();
    }

    private void ConfigureUserShellSize()
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(1, area.Width - 24);
        var maxHeight = Math.Max(1, area.Height - 24);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        MinWidth = Math.Min(820, maxWidth);
        MaxWidth = maxWidth;
        Width = Math.Min(940, maxWidth);
        MinHeight = Math.Min(Math.Max(700, area.Height * 0.9), maxHeight);
        MaxHeight = maxHeight;
        Height = double.NaN;
        Title = "ИТ-Сети | Помощь с компьютером";
    }

    private void ConfigureEngineerShell()
    {
        AdminUnlock.Visibility = Visibility.Collapsed;
        UserShell.Visibility = Visibility.Collapsed;
        AdminShell.Visibility = Visibility.Visible;
        AdminTabs.SelectedIndex = 0;
        AdminNavigation.SelectedIndex = 0;
        if (engineerOnly) ExitAdminButton.Content = "Закрыть окно";
        SizeToContent = SizeToContent.Manual;
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(1, area.Width - 24);
        var maxHeight = Math.Max(1, area.Height - 24);
        MinWidth = Math.Min(880, maxWidth);
        MinHeight = Math.Min(610, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        Width = Math.Min(1200, maxWidth);
        Height = Math.Min(Math.Max(900, area.Height * 0.92), maxHeight);
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        Title = "ИТ-Сети | Диагностика ПК";
    }

    private static string DefaultHistoryDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "history.db");

    private void EventLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null && EventLevelSelector.SelectedIndex >= 0)
            ViewModel.EventFilterLevel = EventLevelSelector.SelectedIndex + 1;
    }

    private async void RefreshSetupAudit_Click(object sender, RoutedEventArgs e) => await RefreshSoftwareCatalogAsync();
    private async Task RefreshSoftwareCatalogAsync()
    {
        if (refreshingSoftware || closed) return;
        refreshingSoftware = true;
        try
        {
            if (ServerSoftwareCatalog.IsConnected)
            {
                ViewModel.SetSetupStatus("Получаем актуальное ПО с сервера...");
                await ServerSoftwareCatalog.RefreshAsync();
                if (!closed) ViewModel.SetSetupStatus("Список ПО обновлён с сервера.");
            }
        }
        catch (Exception ex)
        {
            var error = ApplicationErrorStore.Record("ПО", "Обновление каталога с сервера", ex);
            if (!closed) ViewModel.SetSetupStatus("Не удалось обновить каталог ПО: " + error.Summary);
        }
        finally { refreshingSoftware = false; if (!closed) ViewModel.RefreshSetupAudit(); }
    }
    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckApplicationUpdatesAsync();

    private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckApplicationUpdatesAsync();
        await InstallAvailableUpdateAsync(confirm: true);
    }

    private async Task InstallAvailableUpdateAsync(bool confirm)
    {
        if (!ViewModel.CanInstallApplicationUpdate || ViewModel.ApplicationUpdateStatus.StartsWith("Установлена последняя", StringComparison.OrdinalIgnoreCase)) return;
        if (!ViewModel.ApplicationUpdateStatus.StartsWith("Доступна версия", StringComparison.OrdinalIgnoreCase)) return;
        if (confirm && MessageBox.Show(this,
            "Откроется окно хода обновления. Установщик будет скачан и проверен по SHA-256. После этого приложение закроется, чтобы Windows могла заменить файлы.",
            "Обновление приложения", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var progressWindow = new UpdateProgressWindow(ViewModel.ApplicationUpdateTarget, ViewModel.RequestApplicationUpdateAsync)
        {
            Owner = this
        };
        if (progressWindow.ShowDialog() == true)
        {
            try { await StartUpdateProgressHostAsync(ViewModel.ApplicationUpdateTarget); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть окно установки: " + ex.Message
                    + "\nУстановщик уже проверен; обновление продолжится после закрытия приложения.",
                    "Обновление приложения", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Application.Current.Shutdown();
        }
    }

    private static async Task StartUpdateProgressHostAsync(string targetVersion)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Backend", "UpdateProgressHost.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Окно обновления не включено в пакет.", script);
        var ready = Path.Combine(Path.GetTempPath(), "ITSeti-update-" + Guid.NewGuid().ToString("N") + ".ready");
        var status = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSeti", "Maintenance", "Updates", "last-result.txt");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", script,
                     "-StatusPath", status, "-ReadyPath", ready, "-Version", targetVersion })
            start.ArgumentList.Add(argument);
        using var monitor = Process.Start(start) ?? throw new InvalidOperationException("Windows не запустила окно обновления.");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline)
            {
                if (monitor.HasExited) throw new InvalidOperationException("Окно обновления завершилось до запуска.");
                await Task.Delay(100);
            }
            if (!File.Exists(ready)) throw new TimeoutException("Окно обновления не ответило за 8 секунд.");
            var result = await File.ReadAllTextAsync(ready);
            if (result != "ready") throw new InvalidOperationException(result);
        }
        finally { try { File.Delete(ready); } catch (IOException) { } }
    }
    private async void SaveInventory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.SaveInventoryNumber();
            if (string.IsNullOrWhiteSpace(ViewModel.InventoryNumber)) return;
            using var enrollment = new ServerEnrollmentClient();
            if (await enrollment.TryAutomaticEnrollmentAsync())
            {
                ViewModel.RefreshIdentity();
                return;
            }
            var dialog = new ServerAssignmentDialog { Owner = this };
            if (dialog.ShowDialog() == true) ViewModel.RefreshIdentity();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Инвентарный номер не сохранён", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось определить привязку компании и объекта", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenServerAssignment_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using (var enrollment = new ServerEnrollmentClient())
            {
                if (await enrollment.TryAutomaticEnrollmentAsync())
                {
                    ViewModel.RefreshIdentity();
                    return;
                }
            }
            var dialog = new ServerAssignmentDialog { Owner = this };
            if (dialog.ShowDialog() == true) ViewModel.RefreshIdentity();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Компания и объект", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void RefreshRepairStatus_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshRepairStatus();

    private async void InstallSetup_Click(object sender, RoutedEventArgs e)
    {
        await RunOrganizationInstallAsync(null);
    }

    private async void CreateAdminAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Учётная запись администратора",
            Icon = Icon,
            Width = 455,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            FontFamily = FontFamily
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Создать локальную учётную запись", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(20, 60, 135)), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Используется Admin; если она уже есть — it-seti. Существующую учётную запись включим и проверим права, но её пароль не изменим.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Пароль новой учётной записи", Margin = new Thickness(0, 0, 0, 5) });
        var input = new TextBox { Height = 36, Padding = new Thickness(8, 5, 8, 5), FontSize = 16 };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var create = new Button { Content = "Создать", Padding = new Thickness(14, 8, 14, 8), Background = new SolidColorBrush(Color.FromRgb(50, 110, 255)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        var password = input.Text;
        input.Clear();
        try
        {
            var result = await LocalAdminAccountRunner.CreateAsync(password);
            MessageBox.Show(this, result, "Учётная запись создана", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Учётные записи","Создание локального администратора",ex);
            MessageBox.Show(this, ex.Message, "Учётная запись не создана", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void InstallComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string component, DataContext: OrganizationComponent row }) return;
        if (row.State == "Установлено") await RunOrganizationUninstallAsync(component);
        else await RunOrganizationInstallAsync(component);
    }

    private async Task RunOrganizationUninstallAsync(string component)
    {
        if (!ViewModel.BeginSetupInstall()) return;
        var displayName = ViewModel.SetupComponents.FirstOrDefault(row => row.InstallKey == component)?.Name ?? component;
        try
        {
            if (MessageBox.Show(this, $"Удалить {displayName}? Данные RMS и AnyDesk с идентификаторами сохранятся.", "Удаление ПО", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ViewModel.SetSetupStatus($"Удаление {displayName} выполняется…");
            ViewModel.SetSetupComponentStatus(component, "Удаление…");
            await OrganizationSetupRunner.RunUninstallComponentAsync(component, new Progress<string>(ViewModel.SetSetupStatus));
            ViewModel.RefreshSetupAudit();
            ViewModel.SetSetupComponentStatus(component, "Удалено");
            ViewModel.SetSetupStatus($"{displayName}: удаление завершено.");
            MessageBox.Show(this, $"{displayName}: удаление завершено.", "Удаление ПО", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("ПО","Удаление компонента "+component,ex);
            ViewModel.SetSetupStatus($"Удаление не выполнено: {ex.Message}");
            ViewModel.SetSetupComponentStatus(component, "Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Удаление не выполнено", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ViewModel.EndSetupInstall(); }
    }

    private async Task RunOrganizationInstallAsync(string? component)
    {
        if (!ViewModel.BeginSetupInstall()) return;
        try
        {
            var selected = component is null
                ? ViewModel.SetupComponents.Where(row => row.IsSelected && row.CanSelectInstall).Select(row => row.InstallKey).ToArray()
                : [component];
            if (selected.Length == 0)
            {
                ViewModel.SetSetupStatus("Отметьте компоненты, которые нужно установить или обновить.");
                return;
            }

            var problems = new List<string>();
            foreach (var key in selected)
                problems.AddRange(await OrganizationSetupRunner.CheckAsync(key));
            if (problems.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, problems), "Установка остановлена проверкой комплекта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var names = selected.Select(key => ViewModel.SetupComponents.FirstOrDefault(row => row.InstallKey == key)?.Name ?? key);
            var confirmation = $"Установить последовательно: {string.Join(", ", names)}?";
            if (MessageBox.Show(this, confirmation, "Установка ПО", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var results = new List<string>();
            var componentResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in selected)
            {
                var name = ViewModel.SetupComponents.FirstOrDefault(row => row.InstallKey == key)?.Name ?? key;
                ViewModel.SetSetupStatus($"Устанавливаем {name}…");
                ViewModel.SetSetupComponentStatus(key, "Установка…");
                try
                {
                    var progress = new Progress<string>(message =>
                    {
                        ViewModel.SetSetupStatus(message);
                        ViewModel.SetSetupComponentStatus(key, message);
                    });
                    var exitCode = await OrganizationSetupRunner.RunComponentAsync(key, progress);
                    var result = exitCode == OrganizationSetupRunner.InteractiveInstallerLaunched
                        ? "Открыт обычный установщик. Завершите его и нажмите «Проверить ПО»."
                        : exitCode == 0 ? "Установлено" : $"Ошибка: код {exitCode}";
                    componentResults[key] = result;
                    results.Add($"{name}: {result}.");
                }
                catch (Exception ex)
                {
                    if (ex is not EngineerAuthorizationRequiredException)
                        ApplicationErrorStore.Record("ПО", "Установка компонента " + key, ex);
                    var failureMessage = ex.Message;
                    if (ex.Message.Contains("Silent installer exited with code", StringComparison.OrdinalIgnoreCase) &&
                        ServerSoftwareCatalog.Read().Any(package => package.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) &&
                        MessageBox.Show(this,
                            $"Тихая установка {name} завершилась ошибкой. Открыть обычный проверенный установщик в этом сеансе с системными правами?",
                            "Тихая установка не выполнена", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        try
                        {
                            await OrganizationSetupRunner.LaunchServerPackageForKeyAsync(key,
                                new Progress<string>(ViewModel.SetSetupStatus));
                            const string launched = "Открыт обычный установщик. Завершите его и нажмите «Проверить ПО».";
                            componentResults[key] = launched;
                            results.Add($"{name}: {launched}");
                            continue;
                        }
                        catch (Exception fallbackError)
                        {
                            if (fallbackError is not EngineerAuthorizationRequiredException)
                                ApplicationErrorStore.Record("ПО", "Обычный установщик компонента " + key, fallbackError);
                            failureMessage = fallbackError.Message;
                        }
                    }
                    var result = "Ошибка: " + failureMessage;
                    componentResults[key] = result;
                    results.Add($"{name}: {result}");
                }
            }
            ViewModel.RefreshSetupAudit();
            foreach (var result in componentResults) ViewModel.SetSetupComponentStatus(result.Key, result.Value);
            var failed = results.Any(result => result.Contains("Ошибка:", StringComparison.Ordinal));
            var waitingForManualInstall = results.Any(result => result.Contains("Открыт обычный установщик", StringComparison.Ordinal));
            var message = string.Join(Environment.NewLine, results);
            ViewModel.SetSetupStatus(failed ? "Установка завершена с ошибками. См. результат каждого компонента." :
                waitingForManualInstall ? "Обычный установщик открыт. После завершения нажмите «Проверить ПО»." :
                "Выбранные компоненты установлены. Проверьте состояние ПО.");
            MessageBox.Show(this, message, "Установка ПО", MessageBoxButton.OK, failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("ПО", "Установка выбранных компонентов", ex);
            ViewModel.SetSetupStatus("Установка не выполнена: " + ex.Message);
            if (component is not null) ViewModel.SetSetupComponentStatus(component, "Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Установка не выполнена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ViewModel.EndSetupInstall(); }
    }

    private async void LaunchDiskInfo_Click(object sender, RoutedEventArgs e) => await LaunchDiskToolAsync(diskInfo: true);
    private async void LaunchDiskMark_Click(object sender, RoutedEventArgs e) => await LaunchDiskToolAsync(diskInfo: false);
    private async void LaunchHwInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.SetSoftwareActionStatus("Открываем HWiNFO через службу…");
            var result = await MaintenanceServiceClient.LaunchUtilityAsync("HWiNFO", null,
                Process.GetCurrentProcess().SessionId);
            var message = result.Connected ? result.Message : "Служба обслуживания недоступна. Переустановите приложение с включённой службой.";
            ViewModel.SetSoftwareActionStatus(message);
            if (!result.Accepted)
                MessageBox.Show(this, message, "HWiNFO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Утилиты","Открытие HWiNFO",ex);
            ViewModel.SetSoftwareActionStatus("Не удалось открыть HWiNFO: " + ex.Message);
            MessageBox.Show(this, ex.Message, "HWiNFO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async void LaunchTreeSize_Click(object sender, RoutedEventArgs e)
    {
        var volume = ViewModel.SelectedTreeSizeVolume;
        if (string.IsNullOrWhiteSpace(volume)) return;
        try
        {
            ViewModel.SetSoftwareActionStatus("Открываем TreeSize Free через службу…");
            var result = await MaintenanceServiceClient.LaunchUtilityAsync("TreeSize", volume,
                Process.GetCurrentProcess().SessionId);
            var message = result.Connected
                ? result.Message
                : "Служба обслуживания недоступна. Переустановите приложение с включённой службой.";
            ViewModel.SetSoftwareActionStatus(message);
            if (!result.Accepted) MessageBox.Show(this, message, "TreeSize Free", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Утилиты","Открытие TreeSize",ex);
            ViewModel.SetSoftwareActionStatus("Не удалось запустить TreeSize Free: " + ex.Message);
            MessageBox.Show(this, ex.Message, "TreeSize Free", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenToolsFolder_Click(object sender, RoutedEventArgs e)
    {
        var tools = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (!Directory.Exists(tools))
        {
            MessageBox.Show(this, "Папка с диагностическими утилитами не найдена.", "Утилиты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", '"' + tools + '"') { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть каталог утилит", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void LaunchSystemTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        try
        {
            if (key is "settings" or "security")
            {
                SystemToolsLauncher.Launch(key);
                return;
            }
            var result = await MaintenanceServiceClient.LaunchSystemToolAsync(key,
                Process.GetCurrentProcess().SessionId);
            if (!result.Accepted)
                MessageBox.Show(this, result.Connected ? result.Message : "Служба обслуживания недоступна.",
                    "Системное приложение", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Системное приложение", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenEngineerLowSpaceScan_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.OpenLowSpaceScanAsync(engineer: true); }
        catch (Exception ex)
        {
            ApplicationErrorStore.Record("Утилиты", "TreeSize для диска из отчёта", ex);
            MessageBox.Show(this, ex.Message, "TreeSize", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async Task LaunchDiskToolAsync(bool diskInfo)
    {
        try
        {
            var key = diskInfo ? "CrystalDiskInfo" : "CrystalDiskMark";
            var result = await MaintenanceServiceClient.LaunchUtilityAsync(key, null,
                Process.GetCurrentProcess().SessionId);
            var message = result.Connected ? result.Message : "Служба обслуживания недоступна. Переустановите приложение с включённой службой.";
            ViewModel.SetSoftwareActionStatus(message);
            if (!result.Accepted)
                MessageBox.Show(this, message, "Дисковая утилита", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.SetSoftwareActionStatus("Не удалось запустить дисковую утилиту: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Дисковая утилита", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void ExitAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (engineerOnly)
        {
            Close();
            return;
        }
        AdminShell.Visibility = Visibility.Collapsed;
        UserShell.Visibility = Visibility.Visible;
        ConfigureUserShellSize();
    }
    private void RefreshNetwork_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshNetworkAdapters();
    private async void AllowProcess_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessDetails process) return;
        if (sender is Button button) button.IsEnabled = false;
        try { await ViewModel.AllowProcessAsync(process); }
        finally { if (sender is Button control) control.IsEnabled = true; }
    }
    private async void TrustProcessPublisher_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessDetails process || !process.CanTrustPublisher) return;
        if (sender is Button button) button.IsEnabled = false;
        try { await ViewModel.TrustProcessPublisherAsync(process); }
        finally { if (sender is Button control) control.IsEnabled = process.CanTrustPublisher; }
    }
    private async void LoadMoreHistory_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadMoreHistoryAsync();
    private void RefreshDebug_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshDebug();
    private void OpenDebugDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpenDebugDirectory) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", '"' + ViewModel.DebugDirectory + '"') { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть папку проверки", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void DebugFile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: DebugFile file } || !ViewModel.DebugFiles.Contains(file) || !File.Exists(file.Path)) return;
        try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { file.Path } }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть файл", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
