using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    public partial class MainWindow
    {
        private Win7Release availableUpdate;
        private string DiskInfoPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "CrystalDiskInfo", Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe"); } }
        private string DiskMarkPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "CrystalDiskMark9", Environment.Is64BitOperatingSystem ? "DiskMark64.exe" : "DiskMark32.exe"); } }
        private string HwInfoPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "HWiNFO", "HWiNFO32.exe"); } }
        private string TreeSizePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "TreeSize", "TreeSize.exe"); } }
        private string RepairLogPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSetiMaintenanceWin7", "sfc-last.txt"); } }

        private void PopulateTools()
        {
            MyTicketsButton.Visibility = Visibility.Collapsed;
            SupportButton.Visibility = Visibility.Collapsed;
            UserCleanupButton.ToolTip = "Очищаются временные файлы, корзина, кэш эскизов, интернет-кэш и кэш Direct3D текущего пользователя; системные категории запускаются отдельно от SYSTEM.";
            UserCleanupHint.Text = "Профиль, эскизы, интернет- и Direct3D-кэш, корзина";
            MaintenanceCleanupHint.Text = "Очищаются категории профиля и корзина текущего пользователя, затем доступные Windows категории дампов, установочных файлов, обновлений и пакетов драйверов. Личные загрузки и профили браузеров не затрагиваются.";
            MaintenanceRepairHint.Text = "Доступна проверка системных файлов SFC. Автоматической перезагрузки нет.";
            SetupInstallSection.Visibility = Visibility.Collapsed;
            SetupComponentsList.ItemTemplate = (DataTemplate)FindResource("SoftwareAuditOnlyTemplate");
            SoftwareComponentHeader.ColumnDefinitions[3].Width = new GridLength(0);
            SoftwareComponentHeader.ColumnDefinitions[4].Width = new GridLength(0);
            ApplicationUpdateSection.Visibility = engineer ? Visibility.Visible : Visibility.Collapsed;
            ApplicationUpdateHint.Visibility = Visibility.Collapsed;
            view.Set("CanCheckApplicationUpdates", engineer);
            view.Set("ApplicationUpdateStatus", "Обновления Windows 7 проверяются отдельно от основной версии.");
            LaunchDiskMarkButton.Visibility = engineer && File.Exists(DiskMarkPath) ? Visibility.Visible : Visibility.Collapsed;
            TreeSizeVolumeSelector.Visibility = Visibility.Collapsed;
            LaunchTreeSizeButton.Visibility = Visibility.Collapsed;
            OpenLowSpaceScanButton.Visibility = File.Exists(TreeSizePath) ? Visibility.Visible : Visibility.Collapsed;
            var definitions = new[] {
                new[] { "Управление компьютером", "compmgmt.msc" }, new[] { "Групповые политики", "gpedit.msc" },
                new[] { "Терминал", "cmd.exe" }, new[] { "Панель управления", "control.exe" },
                new[] { "Программы", "appwiz.cpl" }, new[] { "Сетевые адаптеры", "ncpa.cpl" },
                new[] { "Сведения о системе", "msinfo32.exe" } };
            view.Set("SystemTools", Rows(definitions.Select(x => new Win7Row { Key = x[1], Title = x[0], Icon = "●", Available = true, ToolTip = x[0] })));
            view.Set("WindowsDefenderStatus", "Microsoft Defender в Windows 7 отсутствует.");
            view.Set("WindowsUpdatePolicyStatus", "Текущую настройку можно изменить и вернуть.");
            view.Set("WindowsUpdateStatus", "Проверка обновлений не запускалась.");
            view.Set("StandaloneRepairStatus", "В Windows 7 доступен SFC.");
            view.Set("CleanupStatus", "Очистка не запускалась.");
            view.Set("CanRunCleanup", true);
            LaunchDiskMarkButton.IsEnabled = engineer && File.Exists(DiskMarkPath);
            WindowsDefenderToggleButton.Visibility = Visibility.Collapsed;
            WindowsDefenderInfoSection.Visibility = Visibility.Collapsed;
            CreateAdminAccountButton.IsEnabled = false;
        }

        private async Task RunActionAsync(string description, string target, Func<string> action)
        {
            view.Set(target, description + "...");
            try { view.Set(target, await Task.Run(action)); }
            catch (Exception ex) { view.Set(target, "Не выполнено: " + ex.Message); }
        }

        private void SaveInventory_Click(object sender, RoutedEventArgs e)
        {
            var value = InventoryInput.Text.Trim();
            if (!Regex.IsMatch(value, @"^\d{4}$")) { MessageBox.Show(this, "Укажите четыре цифры инвентарного номера.", "Инвентарный номер"); return; }
            try
            {
                var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ITSeti", "Maintenance", "inventory.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(shared));
                File.WriteAllText(shared, value, new UTF8Encoding(false));
                if (current != null)
                {
                    current.InventoryNumber = value;
                    File.WriteAllText(Path.Combine(dataPath, "latest.json"), current.ToJson(), new UTF8Encoding(false));
                    Render(current);
                }
                view.Set("Status", "Инвентарный номер сохранён для всех пользователей.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить номер"); }
        }

        private void CopyInventory_Click(object sender, RoutedEventArgs e) { Copy(current == null ? null : current.InventoryNumber); }
        private void CopyRms_Click(object sender, RoutedEventArgs e) { Copy(current == null ? null : current.RmsId); }
        private void CopyAnyDesk_Click(object sender, RoutedEventArgs e) { Copy(current == null ? null : current.AnyDeskId); }
        private void Copy(string value) { if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value); }

        private void ShowSupport_Click(object sender, RoutedEventArgs e)
        { ShowContacts_Click(sender, e); }

        private void ShowMyTickets_Click(object sender, RoutedEventArgs e) { }

        private void ShowContacts_Click(object sender, RoutedEventArgs e)
        {
            var text = "Инв. номер: " + (current == null ? "не указан" : current.InventoryNumber ?? "не указан") + Environment.NewLine +
                "RMS: " + (current == null ? "не найден" : current.RmsId ?? "не найден") + Environment.NewLine +
                "AnyDesk: " + (current == null ? "не найден" : current.AnyDeskId ?? "не найден");
            var window = new Window { Title = "Контакты ИТ-Сети", Owner = this, Width = 470, Height = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "+7 (343) 243-57-63 · кнопка 2\nsupport@it-seti.ru", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top, Height = 145, FontSize = 16, Padding = new Thickness(8) });
            var copy = new Button { Content = "Копировать всё", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            copy.Click += (s, a) =>
            {
                try { Clipboard.SetDataObject(text, true); window.Close(); }
                catch (Exception ex) { MessageBox.Show(window, "Не удалось скопировать данные: " + ex.Message, "Буфер обмена"); }
            };
            panel.Children.Add(copy);
            window.Content = panel;
            window.ShowDialog();
        }

        private async void OpenServerAssignment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var enrollment = new Win7ServerEnrollmentClient())
                    if (await enrollment.TryAutomaticEnrollmentAsync()) { if (current != null) Render(current); return; }
                new Win7ServerEnrollmentDialog(this).ShowDialog();
                if (current != null) Render(current);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть привязку ПК"); }
        }

        private void ShowAllIssues_Click(object sender, RoutedEventArgs e) { }
        private void OpenLowSpaceScan_Click(object sender, RoutedEventArgs e) { LaunchTreeSize_Click(sender, e); }
        private void OpenEngineerLowSpaceScan_Click(object sender, RoutedEventArgs e) { LaunchTreeSize_Click(sender, e); }
        private async void RunUserFull_Click(object sender, RoutedEventArgs e) { await CheckAsync(true); }
        private void StopFullCheck_Click(object sender, RoutedEventArgs e) { StopCheck(); }
        private async void RunFull_Click(object sender, RoutedEventArgs e) { await CheckAsync(true); }
        private async void RunCleanup_Click(object sender, RoutedEventArgs e)
        {
            view.Set("CleanupStatus", "Очистка запускается службой SYSTEM...");
            var service = await Win7MaintenanceServiceClient.CleanupAsync();
            if (service.Connected)
            {
                view.Set("CleanupStatus", service.Accepted ? service.Message : "Очистка не запущена: " + service.Message);
                if (service.Accepted) await WaitForServiceActionAsync("Очистка", service.RunId);
                return;
            }
            await RunActionAsync("Очистка профиля и системных временных файлов", "CleanupStatus",
                () => LegacyMaintenance.CleanupAfterFullCheck(cleanupUserSid, System.Threading.CancellationToken.None));
        }

        private void RefreshNetwork_Click(object sender, RoutedEventArgs e) { if (current != null) Render(current); }
        private void EventLevel_Changed(object sender, RoutedEventArgs e) { RenderEvents(); }
        private void RefreshSetupAudit_Click(object sender, RoutedEventArgs e) { RefreshSoftware(); }
        private void RefreshDebug_Click(object sender, RoutedEventArgs e) { UpdateDebug(); }
        private void RefreshMaintenance_Click(object sender, RoutedEventArgs e) { view.Set("CanOpenRepairLog", File.Exists(RepairLogPath)); }

        private void UpdateDebug()
        {
            var errors=LegacyApplicationErrors.Read();
            view.Set("DebugSource",errors.Length==0?"Ошибок приложения не зарегистрировано":"Зарегистрировано ошибок: "+errors.Length);
            view.Set("DebugStage",errors.Length==0?"—":errors[0].OccurredAt);
            view.Set("DebugErrorLine",errors.Length==0?"":errors[0].Summary);
            view.Set("DebugSteps",Rows(errors.Select(error=>new Win7Row{Name=error.OccurredAt+" · "+error.Component,State=error.Operation,Detail=error.Summary+"\nКод: "+error.Id+"\n"+error.TechnicalDetails})));
            var files = new[] { Path.Combine(dataPath, "latest.json"), Path.Combine(dataPath, "latest.txt"),
                Path.Combine(ScheduledCheckRunner.ResultsRoot, "latest.json"),
                Path.Combine(ScheduledCheckRunner.ResultsRoot, "scheduled-error.txt"),
                Path.Combine(ScheduledCheckRunner.ResultsRoot, "server-upload-error.txt"), LegacyApplicationErrors.PathName, RepairLogPath };
            view.Set("DebugFiles", Rows(files.Where(File.Exists).Select(x => new Win7Row { Name = Path.GetFileName(x), Path = x })));
            view.Set("CanOpenDebugDirectory", Directory.Exists(dataPath));
        }

        private void DebugFile_DoubleClick(object sender, RoutedEventArgs e)
        {
            var row = (sender as DataGrid) == null ? null : (sender as DataGrid).SelectedItem as Win7Row;
            if (row != null && File.Exists(row.Path)) Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        private void OpenDebugDirectory_Click(object sender, RoutedEventArgs e) { OpenDirectory(dataPath); }
        private void OpenToolsFolder_Click(object sender, RoutedEventArgs e) { OpenDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools")); }
        private void OpenDirectory(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }

        private async void LaunchDiskInfo_Click(object sender, RoutedEventArgs e) { await LaunchUtilityAsync("CrystalDiskInfo", DiskInfoPath); }
        private async void LaunchDiskMark_Click(object sender, RoutedEventArgs e) { await LaunchUtilityAsync("CrystalDiskMark", DiskMarkPath); }
        private async void LaunchHwInfo_Click(object sender, RoutedEventArgs e) { await LaunchUtilityAsync("HWiNFO", HwInfoPath); }
        private async void LaunchTreeSize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!File.Exists(TreeSizePath)) throw new FileNotFoundException("TreeSize Free не включён в установщик Windows 7.", TreeSizePath);
                var service = await Win7MaintenanceServiceClient.LaunchUtilityAsync("TreeSize", null, Process.GetCurrentProcess().SessionId);
                if (service.Connected)
                {
                    if (!service.Accepted) MessageBox.Show(this, service.Message, "Запуск TreeSize", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(TreeSizePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(TreeSizePath) });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Запуск TreeSize"); }
        }
        private async Task LaunchUtilityAsync(string key, string fallbackPath)
        {
            if (!engineer) return;
            try
            {
                var service = await Win7MaintenanceServiceClient.LaunchUtilityAsync(key, null, Process.GetCurrentProcess().SessionId);
                if (service.Connected)
                {
                    if (!service.Accepted) MessageBox.Show(this, service.Message, "Запуск утилиты", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                LaunchFile(fallbackPath);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Запуск утилиты"); }
        }
        private void LaunchFile(string file)
        {
            if (!engineer) return;
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException("Утилита не включена в комплект.", file);
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Запуск утилиты"); }
        }

        private async void LaunchSystemTool_Click(object sender, RoutedEventArgs e)
        {
            if (!engineer) return;
            var file = (sender as Button) == null ? null : (sender as Button).Tag as string;
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var service = await Win7MaintenanceServiceClient.LaunchSystemToolAsync(file, Process.GetCurrentProcess().SessionId);
                if (service.Connected)
                {
                    if (!service.Accepted) MessageBox.Show(this, service.Message, "Системное приложение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var executable = file.EndsWith(".msc", StringComparison.OrdinalIgnoreCase) ? "mmc.exe" : file.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) ? "control.exe" : file;
                var arguments = executable == "mmc.exe" || executable == "control.exe" && file != "control.exe" ? file : "";
                Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory });
            }
            catch (Exception ex) { view.Set("Status", "Не удалось открыть " + file + ": " + ex.Message); }
        }

        private async void CheckWindowsUpdates_Click(object sender, RoutedEventArgs e) { await RunActionAsync("Поиск обновлений Windows", "WindowsUpdateStatus", LegacyMaintenance.CheckWindowsUpdates); }
        private async void DisableWindowsUpdates_Click(object sender, RoutedEventArgs e) { if (engineer) await RunActionAsync("Отключение автообновлений", "WindowsUpdatePolicyStatus", LegacyMaintenance.DisableAutomaticUpdates); }
        private async void RestoreWindowsUpdates_Click(object sender, RoutedEventArgs e) { if (engineer) await RunActionAsync("Восстановление настройки", "WindowsUpdatePolicyStatus", LegacyMaintenance.RestoreAutomaticUpdates); }
        private void ToggleWindowsDefender_Click(object sender, RoutedEventArgs e) { }
        private async void RunRepair_Click(object sender, RoutedEventArgs e)
        {
            if (!engineer) return;
            var service = await Win7MaintenanceServiceClient.RepairAsync();
            if (service.Connected)
            {
                view.Set("StandaloneRepairStatus", service.Accepted ? service.Message : "SFC не запущен: " + service.Message);
                if (service.Accepted) await WaitForServiceActionAsync("SFC", service.RunId);
            }
            else
                await RunActionAsync("SFC проверяет системные файлы", "StandaloneRepairStatus", LegacyMaintenance.RunSfc);
            view.Set("CanOpenRepairLog", File.Exists(RepairLogPath));
        }

        private async Task WaitForServiceActionAsync(string name, string runId)
        {
            var started = DateTime.UtcNow;
            var deadline = started.AddHours(2);
            while (true)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    var timeout = name + " не завершено за 2 часа. Проверьте состояние службы.";
                    if (name == "Очистка") view.Set("CleanupStatus", timeout);
                    else view.Set("StandaloneRepairStatus", timeout);
                    return;
                }
                var status = Win7MaintenanceServiceHost.ReadStatus();
                DateTime stamp;
                var recent = status != null && (string.IsNullOrWhiteSpace(runId) || string.Equals(status.RunId, runId, StringComparison.OrdinalIgnoreCase)) &&
                    DateTime.TryParse(status.StartedAtUtc, null, DateTimeStyles.RoundtripKind, out stamp) &&
                    stamp.ToUniversalTime() >= started.AddSeconds(-2);
                if (recent && string.Equals(status.State, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    var message = status.Message ?? (name + " завершено.");
                    if (name == "Очистка") view.Set("CleanupStatus", message);
                    else view.Set("StandaloneRepairStatus", message);
                    return;
                }
                if (recent && string.Equals(status.State, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var message = "Не выполнено: " + (status.Error ?? status.Message ?? "служба вернула ошибку.");
                    if (name == "Очистка") view.Set("CleanupStatus", message);
                    else view.Set("StandaloneRepairStatus", message);
                    return;
                }
                if (recent && string.Equals(status.State, "Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    var message = name + " остановлено пользователем.";
                    if (name == "Очистка") view.Set("CleanupStatus", message);
                    else view.Set("StandaloneRepairStatus", message);
                    return;
                }
                await Task.Delay(500);
            }
        }
        private void OpenRepairLog_Click(object sender, RoutedEventArgs e) { if (File.Exists(RepairLogPath)) Process.Start(new ProcessStartInfo(RepairLogPath) { UseShellExecute = true }); }
        private void ShowRepairDetails_Click(object sender, RoutedEventArgs e) { MessageBox.Show(this, Convert.ToString(view.Get("StandaloneRepairStatus")), "Восстановление"); }
        private void ShowCleanupDetails_Click(object sender, RoutedEventArgs e) { MessageBox.Show(this, Convert.ToString(view.Get("CleanupStatus")), "Очистка"); }

        private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (!engineer) return;
            view.Set("CanCheckApplicationUpdates", false);
            view.Set("CanInstallApplicationUpdate", false);
            view.Set("ApplicationUpdateStatus", "Проверка версии Windows 7 на сервере...");
            try
            {
                availableUpdate = await Win7ServerUpdater.CheckAsync();
                view.Set("ApplicationUpdateStatus", availableUpdate == null
                    ? "Установлена последняя версия Windows 7 с сервера."
                    : "Доступна версия " + availableUpdate.Version + " для Windows 7.");
                view.Set("CanInstallApplicationUpdate", availableUpdate != null);
            }
            catch (Exception ex)
            {
                availableUpdate = null;
                view.Set("ApplicationUpdateStatus", "Не удалось проверить обновление Windows 7: " + ex.Message);
            }
            finally { view.Set("CanCheckApplicationUpdates", true); }
        }

        private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!engineer || availableUpdate == null) return;
            view.Set("CanInstallApplicationUpdate", false);
            view.Set("CanCheckApplicationUpdates", false);
            try
            {
                await Win7ServerUpdater.StartInstallAsync(availableUpdate,
                    new Progress<string>(message => view.Set("ApplicationUpdateStatus", message)));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                view.Set("ApplicationUpdateStatus", "Обновление не выполнено: " + ex.Message);
                view.Set("CanInstallApplicationUpdate", true);
                view.Set("CanCheckApplicationUpdates", true);
            }
        }
        private void InstallComponent_Click(object sender, RoutedEventArgs e) { }
        private void InstallSetup_Click(object sender, RoutedEventArgs e) { }
        private void CreateAdminAccount_Click(object sender, RoutedEventArgs e) { }

        private void ShowAdminPrompt_Click(object sender, RoutedEventArgs e)
        {
            AdminPassword.Clear();
            AdminVisiblePassword.Clear();
            AdminError.Visibility = Visibility.Collapsed;
            AdminUnlock.Visibility = Visibility.Visible;
            AdminPassword.Focus();
        }
        private void CancelAdmin_Click(object sender, RoutedEventArgs e) { AdminUnlock.Visibility = Visibility.Collapsed; }
        private void ExitAdmin_Click(object sender, RoutedEventArgs e) { Close(); }
        private void AdminPassword_Changed(object sender, RoutedEventArgs e) { AdminError.Visibility = Visibility.Collapsed; }
        private void AdminVisiblePassword_Changed(object sender, RoutedEventArgs e) { AdminError.Visibility = Visibility.Collapsed; }
        private void AdminPassword_PreviewKeyDown(object sender, RoutedEventArgs e) { AdminCapsLockStatus.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed; }
        private void AdminPassword_KeyDown(object sender, RoutedEventArgs e) { if ((e as KeyEventArgs) != null && ((KeyEventArgs)e).Key == Key.Enter) UnlockAdmin_Click(sender, e); }
        private void ToggleAdminPassword_Click(object sender, RoutedEventArgs e)
        {
            if (AdminVisiblePassword.Visibility == Visibility.Visible)
            {
                AdminPassword.Password = AdminVisiblePassword.Text;
                AdminVisiblePassword.Visibility = Visibility.Collapsed;
                AdminPassword.Focus();
            }
            else
            {
                AdminVisiblePassword.Text = AdminPassword.Password;
                AdminVisiblePassword.Visibility = Visibility.Visible;
                AdminVisiblePassword.Focus();
            }
        }
        private void UnlockAdmin_Click(object sender, RoutedEventArgs e)
        {
            var password = AdminVisiblePassword.Visibility == Visibility.Visible ? AdminVisiblePassword.Text : AdminPassword.Password;
            AdminPassword.Clear();
            AdminVisiblePassword.Clear();
            if (!string.Equals(password, "itseti", StringComparison.Ordinal))
            {
                ShowAdminError("Неверный пароль инженера.");
                AdminPassword.Focus();
                return;
            }
            engineer = true;
            SetEngineerMode(true);
            RefreshSoftware();
            if (current != null) Render(current);
        }
        private void ShowAdminError(string text) { AdminError.Text = text; AdminError.Visibility = Visibility.Visible; }
    }
}
