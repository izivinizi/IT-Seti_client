using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly FullDiagnosticsRunner fullRunner = new();
    private readonly TaskCompletionSource initializationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string historyDatabasePath;
    private readonly bool engineerOnly;
    private bool closeAfterMaintenance;
    private bool exitForUpdate;
    private bool handingOffToEngineer;
    private DispatcherOperation? progressScroll;
    private bool closed;
    private readonly DispatcherTimer liveMetricsTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    public MainWindow() : this(DefaultHistoryDatabasePath(), false) { }

    public MainWindow(string databasePath) : this(databasePath, false) { }

    public MainWindow(string databasePath, bool engineerOnly)
    {
        InitializeComponent();
        historyDatabasePath = Path.GetFullPath(databasePath);
        this.engineerOnly = engineerOnly;
        AdminAccount.Text = Environment.MachineName + "\\" + Environment.UserName;
        if (!engineerOnly) ConfigureUserShellSize();
        ViewModel = new MainViewModel(new WindowsDiagnosticsRunner(), new SqliteHistoryStore(historyDatabasePath), fullRunner);
        ViewModel.SetWindowsDefenderAccess(EngineerWindowLauncher.IsAdministrator());
        DataContext = ViewModel;
        if (engineerOnly) ConfigureEngineerShell();
        ViewModel.PropertyChanged += (_, _) =>
        {
            if (closeAfterMaintenance && !ViewModel.IsBackgroundMaintenanceRunning)
                Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        };
        ViewModel.CheckProgressEntries.CollectionChanged += (_, _) => QueueProgressScroll();
        CheckProgressList.IsVisibleChanged += (_, _) => QueueProgressScroll();
        liveMetricsTimer.Tick += async (_, _) => await ViewModel.RefreshLiveStatusAsync();
        Closed += (_, _) => { closed = true; liveMetricsTimer.Stop(); progressScroll?.Abort(); };
        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            await ViewModel.RefreshLiveStatusAsync();
            liveMetricsTimer.Start();
            if (this.engineerOnly)
            {
                ViewModel.RefreshSetupAudit();
                ViewModel.RefreshIdentity();
            }
            _ = ViewModel.RefreshWindowsDefenderStatusAsync();
            initializationCompleted.TrySetResult();
        };
        Closing += (_, e) =>
        {
            if (handingOffToEngineer) return;
            if (ViewModel.IsBusy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Диагностика ещё выполняется. Сверните окно и дождитесь результата.", "Проверка выполняется", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (ViewModel.IsBackgroundMaintenanceRunning && !exitForUpdate)
            {
                e.Cancel = true;
                closeAfterMaintenance = true;
                Hide();
            }
        };
    }

    public async Task RunScheduledQuickCheckAsync()
    {
        await initializationCompleted.Task;
        await ViewModel.RunScheduledUserQuickAsync();
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
        AdminTabs.SelectedItem = ProgressTab;
        await ViewModel.RunFullAsync();
    }
    private async void RunQuickFull_Click(object sender, RoutedEventArgs e)
    {
        AdminTabs.SelectedItem = ProgressTab;
        await ViewModel.RunQuickFullAsync();
    }
    private async void RunUserFull_Click(object sender, RoutedEventArgs e) => await ViewModel.RunUserFullAsync();
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
    private void ShowSupport_Click(object sender, RoutedEventArgs e) => new SupportDialog(ViewModel.InventoryNumber, ViewModel.RmsId, ViewModel.AnyDeskId) { Owner = this }.ShowDialog();
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
        AdminPassword.Clear();
        AdminAccount.Text = Environment.MachineName + "\\" + Environment.UserName;
        AdminError.Visibility = Visibility.Collapsed;
        AdminUnlock.Visibility = Visibility.Visible;
        AdminPassword.Focus();
        _ = PopulateAdminAccountsAsync(AdminAccount.Text);
    }

    private async Task PopulateAdminAccountsAsync(string defaultAccount)
    {
        var candidates = await Task.Run(WindowsAdminAccountDiscovery.FindCandidates);
        if (AdminUnlock.Visibility != Visibility.Visible) return;

        var typedAccount = AdminAccount.Text;
        var keepCurrent = !string.Equals(typedAccount, defaultAccount, StringComparison.OrdinalIgnoreCase);
        AdminAccount.ItemsSource = candidates;
        if (keepCurrent)
        {
            AdminAccount.Text = typedAccount;
            return;
        }

        var currentAccount = candidates.FirstOrDefault(account => account.EndsWith("\\" + Environment.UserName, StringComparison.OrdinalIgnoreCase));
        var selected = currentAccount
            ?? candidates.FirstOrDefault(account => account.EndsWith("\\Admin", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(account => account.EndsWith("\\it-seti", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(account => account.EndsWith("\\user", StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : defaultAccount);
        AdminAccount.Text = selected;
    }
    private void CancelAdmin_Click(object sender, RoutedEventArgs e)
    {
        AdminPassword.Clear();
        AdminUnlock.Visibility = Visibility.Collapsed;
    }
    private async void AdminPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await UnlockAdminAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelAdmin_Click(sender, e); e.Handled = true; }
    }
    private async void UnlockAdmin_Click(object sender, RoutedEventArgs e) => await UnlockAdminAsync();

    private async Task UnlockAdminAsync()
    {
        var password = AdminPassword.SecurePassword.Copy();
        AdminPassword.Clear();
        if (!MatchesPassword(password, "itseti"))
        {
            var account = AdminAccount.Text;
            UnlockButton.IsEnabled = false;
            try
            {
                var launch = await Task.Run(() => EngineerWindowLauncher.TryStartWithWindowsCredentials(
                    account, password, historyDatabasePath));
                if (launch.Process is null)
                {
                    AdminError.Text = FormatCredentialError(launch.ErrorCode);
                    AdminError.Visibility = Visibility.Visible;
                    AdminPassword.Focus();
                    return;
                }
                HandoffToEngineer(launch.Process);
            }
            catch (Win32Exception ex)
            {
                AdminError.Text = FormatCredentialError(ex.NativeErrorCode);
                AdminError.Visibility = Visibility.Visible;
                AdminPassword.Focus();
            }
            catch (Exception ex)
            {
                AdminError.Text = "Не удалось открыть инженерское окно: " + ex.Message;
                AdminError.Visibility = Visibility.Visible;
                AdminPassword.Focus();
            }
            finally
            {
                password.Dispose();
                UnlockButton.IsEnabled = true;
            }
            return;
        }

        password.Dispose();
        ShowEngineerShell();
    }

    private static string FormatCredentialError(int errorCode) => errorCode switch
    {
        1326 => "Windows не приняла учётную запись или пароль. Введите пароль Windows, не PIN-код.",
        267 => "Windows не смогла открыть каталог запуска для этой учётной записи. Запустите установленную версию приложения или укажите доступный ей путь.",
        1385 => "Для этой учётной записи запрещён интерактивный вход в Windows. Используйте другую учётную запись администратора.",
        _ => $"Не удалось открыть инженерское окно (код Windows {errorCode}). Проверьте формат имени: пользователь, ПК\\пользователь или ДОМЕН\\пользователь."
    };

    private static bool MatchesPassword(SecureString password, string expected)
    {
        var buffer = Marshal.SecureStringToBSTR(password);
        try
        {
            if (Marshal.ReadInt32(buffer, -sizeof(int)) != expected.Length * sizeof(char)) return false;
            for (var index = 0; index < expected.Length; index++)
                if ((char)Marshal.ReadInt16(buffer, index * sizeof(char)) != expected[index]) return false;
            return true;
        }
        finally { Marshal.ZeroFreeBSTR(buffer); }
    }

    private void ShowEngineerShell()
    {
        ConfigureEngineerShell();
        ViewModel.RefreshSetupAudit();
        ViewModel.RefreshIdentity();
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

    private void HandoffToEngineer(Process process)
    {
        var handled = 0;
        void OnExited()
        {
            if (Interlocked.Exchange(ref handled, 1) != 0) return;
            var exitCode = -1;
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
            Dispatcher.BeginInvoke(() =>
            {
                process.Dispose();
                if (exitCode == 0)
                {
                    handingOffToEngineer = true;
                    Close();
                    return;
                }

                AdminError.Text = exitCode == 1223
                    ? "Запуск инженерского окна отменён."
                    : $"Инженерское окно не запустилось (код {exitCode}).";
                AdminError.Visibility = Visibility.Visible;
                AdminUnlock.Visibility = Visibility.Visible;
                Show();
                Activate();
                AdminPassword.Focus();
            });
        }

        process.Exited += (_, _) => OnExited();
        process.EnableRaisingEvents = true;
        AdminUnlock.Visibility = Visibility.Collapsed;
        Hide();
        if (process.HasExited) OnExited();
    }

    private static string DefaultHistoryDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "history.db");

    private void EventLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null && EventLevelSelector.SelectedIndex >= 0)
            ViewModel.EventFilterLevel = EventLevelSelector.SelectedIndex + 1;
    }

    private void RefreshSetupAudit_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshSetupAudit();
    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckApplicationUpdatesAsync();

    private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckApplicationUpdatesAsync();
        if (!ViewModel.CanInstallApplicationUpdate || ViewModel.ApplicationUpdateStatus.StartsWith("Установлена последняя", StringComparison.OrdinalIgnoreCase)) return;
        if (!ViewModel.ApplicationUpdateStatus.StartsWith("Доступна версия", StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show(this,
            "Приложение закроется. Системная задача скачает установщик последней версии и проверит его SHA-256 перед обновлением.",
            "Обновление приложения", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (await ViewModel.RequestApplicationUpdateAsync())
        {
            exitForUpdate = true;
            Application.Current.Shutdown();
        }
    }
    private void SaveInventory_Click(object sender, RoutedEventArgs e)
    {
        try { ViewModel.SaveInventoryNumber(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Инвентарный номер не сохранён", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            ViewModel.SetSetupStatus($"Удаление не выполнено: {ex.Message}");
            ViewModel.SetSetupComponentStatus(component, "Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Удаление не выполнено", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ViewModel.EndSetupInstall(); }
    }

    private async Task RunOrganizationInstallAsync(string? component)
    {
        if (!ViewModel.BeginSetupInstall()) return;
        var source = ViewModel.SetupSourcePath;
        try
        {
            var problems = (await OrganizationSetupRunner.CheckAsync(source, component)).ToList();
            if (component is null)
            {
                foreach (var bundled in new[] { "WinRAR", "Yandex" })
                    problems.AddRange(await OrganizationSetupRunner.CheckAsync(null, bundled));
            }
            if (problems.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, problems), "Установка остановлена проверкой комплекта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirmation = component is null ? "Установить AnyDesk, RMS, OCS, панель ИТ-Сети, WinRAR и Яндекс Браузер?" : $"Установить компонент {component}?";
            if (MessageBox.Show(this, confirmation, "Установка ПО", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            ViewModel.SetSetupStatus(component is null ? "Установка комплекта выполняется…" : $"Установка {component} выполняется…");
            var progress = new Progress<string>(message =>
            {
                ViewModel.SetSetupStatus(message);
                if (component is not null) ViewModel.SetSetupComponentStatus(component, message);
            });
            if (component is not null) ViewModel.SetSetupComponentStatus(component, "Установка…");
            var exitCode = component is null ? await OrganizationSetupRunner.RunBaseAsync(source!, progress)
                : component is "WinRAR" or "Yandex" ? await OrganizationSetupRunner.RunBundledComponentAsync(component, progress)
                : await OrganizationSetupRunner.RunComponentAsync(source!, component, progress);
            ViewModel.RefreshSetupAudit(source);
            if (component is not null) ViewModel.SetSetupComponentStatus(component, "Установлено");
            if (component is null)
            {
                foreach (var key in new[] { "AnyDesk", "RMS", "OCS", "Panel" }) ViewModel.SetSetupComponentStatus(key, "Установлено");
                foreach (var key in new[] { "WinRAR", "Yandex" })
                {
                    if (ViewModel.SetupComponents.FirstOrDefault(row => row.InstallKey == key)?.State == "Установлено")
                    {
                        ViewModel.SetSetupComponentStatus(key, "Уже установлено");
                        continue;
                    }
                    ViewModel.SetSetupComponentStatus(key, "Установка…");
                    await OrganizationSetupRunner.RunBundledComponentAsync(key, new Progress<string>(message => ViewModel.SetSetupComponentStatus(key, message)));
                    ViewModel.RefreshSetupAudit(source);
                    ViewModel.SetSetupComponentStatus(key, "Установлено");
                }
            }
            var message = exitCode == 0 ? "Установка завершена. Проверьте состояние компонентов." : $"Установщик вернул код {exitCode}. Проверьте C:\\ProgramData\\ITSETI\\install.log.";
            ViewModel.SetSetupStatus(message);
            MessageBox.Show(this, message, "Установка ПО", MessageBoxButton.OK, exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.SetSetupStatus("Установка не выполнена: " + ex.Message);
            if (component is not null) ViewModel.SetSetupComponentStatus(component, "Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Установка не выполнена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ViewModel.EndSetupInstall(); }
    }

    private void ChooseSetupSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка ITSETI-Setup" };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "system", "Install.ps1")))
        {
            MessageBox.Show(this, "В этой папке нет system\\Install.ps1.", "Комплект не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ViewModel.RefreshSetupAudit(dialog.FolderName);
    }
    private async void LaunchDiskInfo_Click(object sender, RoutedEventArgs e) => await LaunchDiskToolAsync(diskInfo: true);
    private async void LaunchDiskMark_Click(object sender, RoutedEventArgs e) => await LaunchDiskToolAsync(diskInfo: false);
    private async void LaunchTreeSize_Click(object sender, RoutedEventArgs e)
    {
        var volume = ViewModel.SelectedTreeSizeVolume;
        if (string.IsNullOrWhiteSpace(volume)) return;
        try
        {
            ViewModel.SetSoftwareActionStatus("Открываем TreeSize Free без запроса UAC…");
            await TreeSizeLauncher.StartScanAsync(volume);
            ViewModel.SetSoftwareActionStatus($"TreeSize Free открыт для {volume} с правами текущей учётной записи.");
        }
        catch (Exception ex)
        {
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

    private void OpenToolsArchive_Click(object sender, RoutedEventArgs e)
    {
        var archive = Path.Combine(AppContext.BaseDirectory, "Tools", "Tools.rar");
        if (!File.Exists(archive))
        {
            MessageBox.Show(this, "Архив Tools.rar не найден в комплекте приложения.", "Утилиты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(archive) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть архив: " + ex.Message, "Архив утилит", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void LaunchSystemTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        try { _ = SystemToolsLauncher.Launch(key); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Системное приложение", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async Task LaunchDiskToolAsync(bool diskInfo)
    {
        try
        {
            var message = await fullRunner.LaunchInteractiveDiskToolAsync(diskInfo);
            ViewModel.SetSoftwareActionStatus(message);
            if (!message.Contains("окно открыто", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("окно уже открыто", StringComparison.OrdinalIgnoreCase))
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
