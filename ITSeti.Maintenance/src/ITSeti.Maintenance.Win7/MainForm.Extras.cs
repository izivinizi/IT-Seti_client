using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ITSeti.Maintenance.Win7
{
    public sealed partial class MainForm
    {
        private readonly DataGridView volumeGrid = MakeGrid("Раздел", "Свободно", "Всего", "Занято");
        private readonly DataGridView smartGrid = MakeGrid("Модель", "Состояние", "Том", "Тип", "Интерфейс", "Наработка");
        private readonly DataGridView networkGrid = MakeGrid("Адаптер", "Тип", "Линк", "Скорость", "IP-адреса");
        private readonly DataGridView processGrid = MakeGrid("Процесс", "ОЗУ", "Путь");
        private readonly DataGridView eventGrid = MakeGrid("Время", "Журнал", "Источник", "ID", "Сообщение");
        private readonly DataGridView historyGrid = MakeGrid("Дата", "Тип", "CPU", "ОЗУ", "Что найдено");
        private readonly DataGridView softwareGrid = MakeGrid("Программа", "Версия", "Состояние");
        private readonly Label benchmarkLabel = TextLabel("Тест скорости ещё не запускался", 11, Ink);
        private readonly RichTextBox historyComparison = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10) };
        private System.Collections.Generic.List<LegacySnapshot> historyItems = new System.Collections.Generic.List<LegacySnapshot>();

        private void BuildExtraTabs(TabControl tabs)
        {
            var disks = Page("Накопители", tabs);
            var diskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 5, ColumnCount = 1 };
            diskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            diskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            diskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            diskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            diskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            disks.Controls.Add(diskLayout);
            var diskHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            diskHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            diskHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            diskHeader.Controls.Add(TextLabel("Последовательный тест системного диска", 12, Blue), 0, 0);
            if (engineerMode)
            {
                var diskInfo = Command("CrystalDiskInfo", false);
                diskInfo.Width = 180;
                diskInfo.Height = 28;
                diskInfo.Click += (sender, args) => OpenDiskInfo();
                diskHeader.Controls.Add(diskInfo, 1, 0);
            }
            diskLayout.Controls.Add(diskHeader, 0, 0);
            diskLayout.Controls.Add(benchmarkLabel, 0, 1);
            diskLayout.Controls.Add(smartGrid, 0, 2);
            diskLayout.Controls.Add(TextLabel("Разделы", 12, Blue), 0, 3);
            diskLayout.Controls.Add(volumeGrid, 0, 4);

            Page("Сеть", tabs).Controls.Add(networkGrid);
            Page("Процессы", tabs).Controls.Add(processGrid);
            Page("События", tabs).Controls.Add(eventGrid);

            var software = Page("ПО", tabs);
            var softwareLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 2, ColumnCount = 1 };
            softwareLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            softwareLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            software.Controls.Add(softwareLayout);
            var refreshSoftware = Command("Обновить список", false);
            refreshSoftware.Width = 170;
            refreshSoftware.Click += (sender, args) => RefreshSoftwareAudit();
            softwareLayout.Controls.Add(refreshSoftware, 0, 0);
            softwareLayout.Controls.Add(softwareGrid, 0, 1);
            tabs.SelectedIndexChanged += (sender, args) => { if (tabs.SelectedTab == software) RefreshSoftwareAudit(); };

            var history = Page("История", tabs);
            var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
            historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            history.Controls.Add(historyLayout);
            historyLayout.Controls.Add(historyGrid, 0, 0);
            historyLayout.Controls.Add(TextLabel("Изменения относительно предыдущей проверки", 12, Blue), 0, 1);
            historyLayout.Controls.Add(historyComparison, 0, 2);
            historyGrid.SelectionChanged += (sender, args) => ShowHistoryComparison();
            RefreshHistory();
            BuildMaintenanceTab(tabs);
        }

        private void OpenDiskInfo()
        {
            if (!engineerMode) return;
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "CrystalDiskInfo",
                Environment.Is64BitOperatingSystem ? "DiskInfo64.exe" : "DiskInfo32.exe");
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException("CrystalDiskInfo отсутствует в комплекте.", file);
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Накопители", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static TabPage Page(string title, TabControl tabs)
        {
            var page = new TabPage(title) { BackColor = Color.White };
            tabs.TabPages.Add(page);
            return page;
        }

        private static DataGridView MakeGrid(params string[] names)
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(223, 230, 235), Font = new Font("Segoe UI", 10),
                ColumnHeadersHeight = 35, RowTemplate = { Height = 32 }
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(239, 244, 248);
            grid.EnableHeadersVisualStyles = false;
            foreach (var name in names) grid.Columns.Add(name, name);
            return grid;
        }

        private void RenderExtraTabs(LegacySnapshot snapshot)
        {
            volumeGrid.Rows.Clear();
            foreach (var disk in snapshot.Disks)
                volumeGrid.Rows.Add(disk.Name + (disk.IsSystem ? " · система" : ""),
                    (disk.FreeBytes / 1073741824.0).ToString("N1") + " ГБ",
                    (disk.TotalBytes / 1073741824.0).ToString("N1") + " ГБ",
                    disk.TotalBytes > 0 ? ((disk.TotalBytes - disk.FreeBytes) * 100.0 / disk.TotalBytes).ToString("N0") + "%" : "—");
            smartGrid.Rows.Clear();
            foreach (var disk in snapshot.SmartDisks)
                smartGrid.Rows.Add(disk.Model, disk.Health, disk.Letters, disk.MediaType, disk.TransferMode,
                    disk.PowerOnHours.HasValue ? disk.PowerOnHours.Value.ToString("N0") + " ч" : "—");
            benchmarkLabel.Text = snapshot.Benchmark == null ? "Тест не запускался" : snapshot.Benchmark.State == "Completed"
                ? string.Format("Чтение {0:N1} МБ/с    Запись {1:N1} МБ/с", snapshot.Benchmark.ReadMbps, snapshot.Benchmark.WriteMbps)
                : snapshot.Benchmark.Error;
            networkGrid.Rows.Clear();
            foreach (var adapter in snapshot.Network ?? new System.Collections.Generic.List<LegacyNetworkAdapter>())
                networkGrid.Rows.Add(adapter.Name, adapter.Kind, adapter.Status, adapter.Speed, adapter.Addresses);
            processGrid.Rows.Clear();
            foreach (var process in snapshot.Processes ?? new System.Collections.Generic.List<LegacyProcess>())
                processGrid.Rows.Add(process.Name, (process.MemoryBytes / 1048576.0).ToString("N0") + " МБ", process.Path);
            eventGrid.Rows.Clear();
            foreach (var item in snapshot.Events)
                eventGrid.Rows.Add(item.Time, item.Log, item.Source, item.Id, item.Message);
        }

        private void RefreshHistory()
        {
            historyItems = LegacyHistoryStore.Load(dataPath);
            historyGrid.Rows.Clear();
            foreach (var item in historyItems)
            {
                var total = item.TotalMemoryGb ?? 0;
                var free = item.FreeMemoryGb ?? 0;
                historyGrid.Rows.Add(item.CheckedAt, item.Kind ?? "Быстрая",
                    item.CpuPercent.HasValue ? item.CpuPercent.Value.ToString("N0") + "%" : "—",
                    total > 0 ? ((total - free) * 100 / total).ToString("N0") + "%" : "—",
                    item.Findings.Count == 0 ? "Замечаний нет" : string.Join("; ", item.Findings.Take(2)));
            }
            ShowHistoryComparison();
        }

        private void RefreshSoftwareAudit()
        {
            softwareGrid.Rows.Clear();
            try
            {
                foreach (var item in LegacySoftwareAudit.Collect())
                    softwareGrid.Rows.Add(item.Name, item.Version, item.State);
            }
            catch (Exception ex)
            {
                softwareGrid.Rows.Add("Не удалось прочитать список программ", "", ex.Message);
            }
        }

        private void ShowHistoryComparison()
        {
            var index = historyGrid.CurrentRow == null ? -1 : historyGrid.CurrentRow.Index;
            if (index < 0 || index >= historyItems.Count) { historyComparison.Text = "Выберите проверку."; return; }
            historyComparison.Text = LegacyHistoryStore.Compare(historyItems[index],
                index + 1 < historyItems.Count ? historyItems[index + 1] : null);
        }
    }
}
