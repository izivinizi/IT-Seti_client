using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    public partial class MainWindow : Window
    {
        private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(20, 65, 158));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(24, 121, 83));
        private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(166, 100, 23));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(161, 57, 57));
        private readonly Win7ViewModel view = new Win7ViewModel();
        private readonly string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "MaintenanceWin7");
        private readonly bool engineer;
        private LegacySnapshot current;
        private List<LegacySnapshot> history = new List<LegacySnapshot>();
        private bool running;
        private bool cpuSampling;
        private DateTime scheduledReportStamp;
        private readonly DispatcherTimer cpuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        public MainWindow(bool engineerMode = false, string previewReport = null)
        {
            engineer = engineerMode;
            if (previewReport != null) dataPath = Path.GetDirectoryName(Path.GetFullPath(previewReport));
            InitializeComponent();
            DataContext = view;
            EventLevelSelector.SelectedIndex = 1;
            EventLevelSelector.IsEnabled = false;
            EventLevelSelector.ToolTip = "В отчёте Windows 7 собираются ошибки и сбои аудита; другие уровни не сохраняются.";
            AdminShell.ColumnDefinitions[1].MinWidth = 0;
            AdminTabs.MinWidth = 0;
            AdminNavigation.SelectedIndex = 0;
            AdminTabs.SelectedIndex = 0;
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.CanResize;
            Width = Math.Min(engineer ? 1320 : 1200, SystemParameters.WorkArea.Width - 24);
            Height = Math.Min(engineer ? 920 : 850, SystemParameters.WorkArea.Height - 24);
            MinWidth = Math.Min(820, Width);
            MinHeight = Math.Min(540, Height);
            view.PropertyChanged += ViewChanged;
            SetEngineerMode(engineer);
            PopulateTools();
            RefreshHistory();
            RefreshSoftware();
            if (previewReport == null)
            {
                LoadCachedReport();
                cpuTimer.Tick += async (sender, args) => await RefreshCpuAsync();
                Loaded += async (sender, args) =>
                {
                    await CheckAsync(false);
                    cpuTimer.Start();
                };
                Closed += (sender, args) => cpuTimer.Stop();
            }
            else
            {
                var snapshot = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                    .Deserialize<LegacySnapshot>(File.ReadAllText(previewReport));
                current = snapshot;
                Render(snapshot);
                UpdateDebug();
            }
            Loaded += (sender, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() => UserContentScroll.ScrollToTop()), DispatcherPriority.ContextIdle);
                ApplyLegacyIconFallback(this);
                UpdateDebug();
            };
        }

        private async Task RefreshCpuAsync()
        {
            if (running || cpuSampling) return;
            cpuSampling = true;
            try
            {
                RefreshScheduledReport();
                var value = await Task.Run(() => CpuLoadSampler.Read());
                view.Set("Cpu", CpuValue(value));
                view.Set("UserCpuLabel", CpuValue(value));
                view.Set("UserCpuValue", value ?? 0d);
                view.Set("CpuStatusBrush", !value.HasValue ? Blue : value >= 85 ? Red : value >= 75 ? Amber : Green);
            }
            catch (Exception ex)
            {
                view.Set("Cpu", "нет данных");
                view.Set("UserCpuLabel", "нет данных");
                view.Set("Status", "Не удалось обновить загрузку CPU: " + ex.Message);
            }
            finally { cpuSampling = false; }
        }

        private void RefreshScheduledReport()
        {
            var file = Path.Combine(ScheduledCheckRunner.ResultsRoot, "latest.json");
            if (!File.Exists(file)) return;
            var stamp = File.GetLastWriteTimeUtc(file);
            if (stamp <= scheduledReportStamp) return;
            var snapshot = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                .Deserialize<LegacySnapshot>(File.ReadAllText(file));
            if (snapshot == null) return;
            scheduledReportStamp = stamp;
            ApplyLocalInventory(snapshot);
            current = snapshot;
            Render(snapshot);
            RefreshHistory();
            UpdateDebug();
        }

        private void SetEngineerMode(bool enabled)
        {
            UserShell.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            AdminShell.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            AdminUnlock.Visibility = Visibility.Collapsed;
            Title = "ИТ-Сети | Обслуживание ПК — Windows 7" + (enabled ? " | инженер" : "");
            view.Set("CanLaunchDiskTools", enabled && File.Exists(DiskInfoPath));
            view.Set("CanLaunchTreeSize", enabled && File.Exists(TreeSizePath));
            view.Set("CanChangeWindowsUpdatePolicy", enabled);
            view.Set("CanRunRepair", enabled);
            view.Set("CanOpenRepairLog", File.Exists(RepairLogPath));
        }

        private async Task CheckAsync(bool full)
        {
            if (running) return;
            running = true;
            foreach (var name in new[] { "IsBusy", "CheckProgressIsRunning" }) view.Set(name, true);
            foreach (var name in new[] { "CanRunFull", "CanRunQuickFull", "CanRun" }) view.Set(name, false);
            view.Set("CheckProgressPhase", full ? "Полная диагностика, SMART и тест диска" : "Диагностика и SMART");
            view.Set("UserStatus", full ? "Идёт полная проверка..." : "Показатели обновляются...");
            Progress("Проверка", full ? "Запущена полная проверка" : "Запущена быстрая проверка");
            try
            {
                var snapshot = await Task.Run(() => LegacyDiagnostics.Collect(full));
                current = snapshot;
                Render(snapshot);
                try
                {
                    Directory.CreateDirectory(dataPath);
                    File.WriteAllText(Path.Combine(dataPath, "latest.json"), snapshot.ToJson(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(dataPath, "latest.txt"), snapshot.ToReport(), new UTF8Encoding(false));
                    LegacyHistoryStore.Save(dataPath, snapshot);
                    RefreshHistory();
                }
                catch (Exception ex) { Progress("История", "Не удалось сохранить отчёт: " + ex.Message); }
                Progress("Проверка", snapshot.Findings.Count == 0 ? "Завершена" : "Завершена, есть замечания: " + snapshot.Findings.Count);
            }
            catch (Exception ex)
            {
                view.Set("UserStatus", "Проверка не завершена: " + ex.Message);
                view.Set("Status", "Ошибка проверки: " + ex.Message);
                Progress("Ошибка", ex.Message);
            }
            finally
            {
                running = false;
                foreach (var name in new[] { "IsBusy", "CheckProgressIsRunning" }) view.Set(name, false);
                foreach (var name in new[] { "CanRunFull", "CanRunQuickFull", "CanRun" }) view.Set(name, true);
                view.Set("CheckProgressPhase", "Проверка завершена");
            }
        }

        private void LoadCachedReport()
        {
            try
            {
                var scheduledFile = Path.Combine(ScheduledCheckRunner.ResultsRoot, "latest.json");
                scheduledReportStamp = File.Exists(scheduledFile) ? File.GetLastWriteTimeUtc(scheduledFile) : DateTime.MinValue;
                var file = new[] { Path.Combine(dataPath, "latest.json"),
                        scheduledFile }
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (!File.Exists(file)) return;
                var snapshot = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                    .Deserialize<LegacySnapshot>(File.ReadAllText(file));
                if (snapshot != null)
                {
                    ApplyLocalInventory(snapshot);
                    current = snapshot;
                    Render(snapshot);
                }
            }
            catch (Exception ex) { view.Set("Status", "Прошлый отчёт недоступен: " + ex.Message); }
        }

        private void ApplyLocalInventory(LegacySnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.InventoryNumber)) return;
            var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSeti", "Maintenance", "inventory.txt");
            foreach (var file in new[] { shared, Path.Combine(dataPath, "inventory.txt") })
            {
                if (!File.Exists(file)) continue;
                var value = File.ReadAllText(file).Trim();
                if (Regex.IsMatch(value, @"^\d{4}$")) { snapshot.InventoryNumber = value; break; }
            }
        }

        private void RefreshHistory()
        {
            history = LegacyHistoryStore.Load(dataPath).Concat(LegacyHistoryStore.Load(ScheduledCheckRunner.ResultsRoot))
                .OrderByDescending(x =>
                {
                    DateTime checkedAt;
                    return DateTime.TryParseExact(x.CheckedAt, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out checkedAt) ? checkedAt : DateTime.MinValue;
                }).Take(30).ToList();
            view.Set("History", Rows(history.Select(x => new Win7Row { Snapshot = x, DateLabel = x.CheckedAt,
                KindLabel = x.Kind, CpuLabel = CpuValue(x.CpuPercent),
                MemoryLabel = x.TotalMemoryGb > 0 ? Value(100 * (x.TotalMemoryGb - x.FreeMemoryGb) / x.TotalMemoryGb, "%") : "—",
                HistoryDetailLabel = x.Findings == null || x.Findings.Count == 0 ? "Замечаний нет" : string.Join("; ", x.Findings.Take(2)) })));
            view.Set("HistoryCount", "Сохранено проверок: " + history.Count);
        }

        private void RefreshSoftware()
        {
            try
            {
                var software = LegacySoftwareAudit.Collect();
                view.Set("SetupComponents", Rows(software.Select(x => new Win7Row { Name = x.Name, State = x.State,
                    Detail = x.Version, Package = "—", InstallLabel = "Недоступно", CanInstall = false })));
                view.Set("SetupSummary", "Проверено программ: " + software.Count + "; установлено: " + software.Count(x => x.State == "Установлено"));
            }
            catch (Exception ex) { view.Set("SetupWarning", "Не удалось проверить ПО: " + ex.Message); }
        }

        private void ViewChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Selected")
            {
                var row = view.Get<Win7Row>("Selected");
                if (row == null || row.Snapshot == null) return;
                view.Set("SelectedHistorySummary", row.Snapshot.Findings == null || row.Snapshot.Findings.Count == 0 ? "Замечаний нет" : string.Join("; ", row.Snapshot.Findings));
                var index = history.IndexOf(row.Snapshot);
                view.Set("Comparison", LegacyHistoryStore.Compare(row.Snapshot, index >= 0 && index + 1 < history.Count ? history[index + 1] : null));
            }
            else if (e.PropertyName == "SelectedEvent")
            {
                var row = view.Get<Win7Row>("SelectedEvent");
                view.Set("EventMessage", row == null ? "" : row.Message);
            }
        }

        private void Progress(string source, string message)
        {
            var list = view.Get<ObservableCollection<Win7Row>>("CheckProgressEntries");
            list.Add(new Win7Row { Time = DateTime.Now.ToString("HH:mm:ss"), Source = source, Message = message });
            CheckProgressList.ScrollIntoView(list.Last());
        }

        private static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("N1") + " ГБ"; }
        private static string Value(double? value, string suffix) { return value.HasValue ? value.Value.ToString("N0") + suffix : "нет данных"; }
        private static string CpuValue(double? value) { return !value.HasValue || value.Value <= 0 ? "нет данных" : value.Value < 1 ? "<1%" : value.Value.ToString("N0") + "%"; }
        private static ObservableCollection<Win7Row> Rows(IEnumerable<Win7Row> source) { return new ObservableCollection<Win7Row>(source); }
    }
}
