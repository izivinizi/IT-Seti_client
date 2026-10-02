using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ITSeti.Maintenance.Win7
{
    public sealed partial class MainForm : Form
    {
        private static readonly Color Blue = Color.FromArgb(23, 70, 150);
        private static readonly Color Ink = Color.FromArgb(23, 36, 52);
        private static readonly Color Muted = Color.FromArgb(82, 99, 118);
        private readonly Button checkButton = Command("Быстрая проверка", false);
        private readonly Button fullButton = Command("Полная проверка", true);
        private readonly Button saveButton = Command("Сохранить отчёт", false);
        private readonly Button copyButton = Command("Копировать", false);
        private readonly Label heading = TextLabel("Диагностика ПК", 18, Blue);
        private readonly Label checkedAt = TextLabel("Проверок пока нет", 10, Muted);
        private readonly Label cpu = TextLabel("CPU  ·  —", 12, Ink);
        private readonly Label memory = TextLabel("ОЗУ  ·  —", 12, Ink);
        private readonly Label systemDisk = TextLabel("Диск C:  ·  —", 12, Ink);
        private readonly Label status = TextLabel("Подготовка проверки", 11, Blue);
        private readonly ListBox findings = new ListBox();
        private readonly TextBox inventoryInput = new TextBox { Width = 74, MaxLength = 4 };
        private readonly RichTextBox report = new RichTextBox();
        private readonly bool engineerMode;
        private readonly string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "MaintenanceWin7");
        private LegacySnapshot current;
        private bool running;

        public MainForm(bool engineer = false)
        {
            engineerMode = engineer;
            Text = "ИТ-Сети Обслуживание ПК · Windows 7 beta" + (engineer ? " · инженер" : "");
            BackColor = Color.White;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(640, area.Width), Math.Min(530, area.Height));
            Size = new Size(Math.Min(1060, area.Width), Math.Min(790, area.Height));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

            var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
            var overviewPage = new TabPage("Обзор") { BackColor = Color.White };
            tabs.TabPages.Add(overviewPage);
            Controls.Add(tabs);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 16), ColumnCount = 1, RowCount = 9 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            overviewPage.Controls.Add(layout);

            var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            title.Controls.Add(heading, 0, 0);
            title.Controls.Add(checkedAt, 0, 1);
            if (!engineer)
            {
                var engineerButton = Command("Режим инженера", false);
                engineerButton.Width = 164;
                engineerButton.Click += OpenEngineer;
                title.Controls.Add(engineerButton, 1, 0);
                title.SetRowSpan(engineerButton, 2);
            }
            layout.Controls.Add(title, 0, 0);

            var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 9, 0, 8) };
            for (var i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            foreach (var label in new[] { cpu, memory, systemDisk })
            {
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleLeft;
                metrics.Controls.Add(label);
            }
            layout.Controls.Add(metrics, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            checkButton.Width = 160;
            fullButton.Width = 155;
            saveButton.Width = 125;
            copyButton.Width = 100;
            actions.Controls.Add(checkButton);
            actions.Controls.Add(fullButton);
            actions.Controls.Add(saveButton);
            actions.Controls.Add(copyButton);
            layout.Controls.Add(actions, 0, 2);
            checkButton.Click += async (sender, args) => await CheckAsync(false);
            fullButton.Click += async (sender, args) => await CheckAsync(true);
            saveButton.Click += SaveReport;
            copyButton.Click += (sender, args) => { if (current != null) Clipboard.SetText(current.ToReport()); };

            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 3);
            var identity = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var inventoryTitle = TextLabel("Инв. номер", 10, Ink);
            inventoryTitle.Dock = DockStyle.None;
            inventoryTitle.Width = 100;
            inventoryTitle.Height = 36;
            identity.Controls.Add(inventoryTitle);
            inventoryInput.Margin = new Padding(0, 6, 8, 0);
            identity.Controls.Add(inventoryInput);
            var inventorySave = Command("Сохранить", false);
            inventorySave.Width = 105;
            inventorySave.Click += SaveInventory;
            identity.Controls.Add(inventorySave);
            var ticketCopy = Command("Копировать данные", false);
            ticketCopy.Width = 180;
            ticketCopy.Click += CopyTicketData;
            identity.Controls.Add(ticketCopy);
            layout.Controls.Add(identity, 0, 4);
            layout.Controls.Add(TextLabel("Что требует внимания", 12, Blue), 0, 5);

            findings.Dock = DockStyle.Fill;
            findings.BorderStyle = BorderStyle.FixedSingle;
            findings.Font = Font;
            findings.HorizontalScrollbar = true;
            layout.Controls.Add(findings, 0, 6);
            layout.Controls.Add(TextLabel("Подробности проверки", 12, Blue), 0, 7);

            report.Dock = DockStyle.Fill;
            report.BorderStyle = BorderStyle.FixedSingle;
            report.BackColor = Color.White;
            report.ForeColor = Ink;
            report.Font = new Font("Segoe UI", 10);
            report.ReadOnly = true;
            report.DetectUrls = false;
            layout.Controls.Add(report, 0, 8);

            BuildExtraTabs(tabs);
            LoadCachedReport();
            Shown += async (sender, args) => await CheckAsync(false);
        }

        private static Label TextLabel(string value, int size, Color color)
        {
            return new Label { Text = value, Font = new Font("Segoe UI", size), ForeColor = color, AutoEllipsis = true, Dock = DockStyle.Fill };
        }

        private static Button Command(string text, bool primary)
        {
            var button = new Button { Text = text, Width = primary ? 190 : 165, Height = 38, Margin = new Padding(0, 3, 9, 0), FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderColor = Blue;
            button.BackColor = primary ? Blue : Color.White;
            button.ForeColor = primary ? Color.White : Blue;
            return button;
        }

        private async Task CheckAsync(bool full)
        {
            if (running) return;
            running = true;
            checkButton.Enabled = false;
            fullButton.Enabled = false;
            status.Text = full ? "Идёт полная проверка и тест системного диска." : "Идёт быстрая проверка. Работать за компьютером можно как обычно.";
            status.ForeColor = Blue;
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
                catch (Exception ex) { status.Text += " Не удалось сохранить локальный отчёт: " + ex.Message; }
            }
            catch (Exception ex)
            {
                status.Text = "Проверка не завершилась: " + ex.Message;
                status.ForeColor = Color.DarkRed;
            }
            finally
            {
                running = false;
                checkButton.Enabled = true;
                fullButton.Enabled = true;
            }
        }

        private void Render(LegacySnapshot snapshot)
        {
            checkedAt.Text = snapshot.ComputerName + "  ·  проверено " + snapshot.CheckedAt;
            inventoryInput.Text = snapshot.InventoryNumber ?? "";
            cpu.Text = "CPU  ·  " + (snapshot.CpuPercent.HasValue ? snapshot.CpuPercent.Value.ToString("N0") + "%" : "нет данных") +
                Environment.NewLine + (snapshot.CpuTemperatureC.HasValue ? snapshot.CpuTemperatureC.Value.ToString("N0") + " °C" : "Температура недоступна") +
                Environment.NewLine + (snapshot.CpuName ?? "Модель не определена");
            memory.Text = "ОЗУ  ·  " + (snapshot.TotalMemoryGb.HasValue ? string.Format("{0:N1} ГБ свободно", snapshot.FreeMemoryGb) : "нет данных") +
                Environment.NewLine + "Запуск: " + (snapshot.LastBootAt ?? "нет данных");
            var drive = snapshot.Disks.Find(x => x.IsSystem);
            systemDisk.Text = "Системный диск  ·  " + (drive == null ? "нет данных" : string.Format("{0:N1} ГБ свободно", drive.FreeBytes / 1073741824.0)) +
                Environment.NewLine + "RMS: " + (snapshot.RmsId ?? "не найден") +
                Environment.NewLine + "AnyDesk: " + (snapshot.AnyDeskId ?? "не найден");
            findings.Items.Clear();
            foreach (var item in snapshot.Findings) findings.Items.Add(item);
            if (snapshot.Findings.Count == 0) findings.Items.Add(snapshot.Unavailable.Count == 0 ? "По доступным данным замечаний нет." : "Есть недоступные показатели; смотрите подробности ниже.");
            report.Text = snapshot.ToReport();
            RenderExtraTabs(snapshot);
            saveButton.Enabled = true;
            copyButton.Enabled = true;
            if (snapshot.Findings.Count > 0)
            {
                status.Text = "Требуют внимания: " + snapshot.Findings.Count + ".";
                status.ForeColor = Color.FromArgb(164, 68, 20);
            }
            else if (snapshot.Unavailable.Count > 0)
            {
                status.Text = "Проверено не всё: " + snapshot.Unavailable.Count + " показателей недоступно.";
                status.ForeColor = Color.FromArgb(164, 96, 17);
            }
            else
            {
                status.Text = "По доступным показателям замечаний нет.";
                status.ForeColor = Color.FromArgb(24, 121, 83);
            }
        }

        private void SaveInventory(object sender, EventArgs args)
        {
            var value = inventoryInput.Text.Trim();
            if (!Regex.IsMatch(value, @"^\d{4}$"))
            {
                MessageBox.Show(this, "Укажите четыре цифры инвентарного номера.", "Инвентарный номер", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Directory.CreateDirectory(dataPath);
                File.WriteAllText(Path.Combine(dataPath, "inventory.txt"), value, new UTF8Encoding(false));
                if (current != null)
                {
                    current.InventoryNumber = value;
                    report.Text = current.ToReport();
                    File.WriteAllText(Path.Combine(dataPath, "latest.json"), current.ToJson(), new UTF8Encoding(false));
                }
                status.Text = "Инвентарный номер сохранён для текущей учётной записи.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить номер", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void CopyTicketData(object sender, EventArgs args)
        {
            if (current == null) return;
            Clipboard.SetText("Инв. номер: " + (current.InventoryNumber ?? "не указан") + Environment.NewLine +
                "RMS: " + (current.RmsId ?? "не найден") + Environment.NewLine +
                "AnyDesk: " + (current.AnyDeskId ?? "не найден"));
            status.Text = "Данные для заявки скопированы.";
        }

        private void LoadCachedReport()
        {
            try
            {
                var path = Path.Combine(dataPath, "latest.json");
                if (File.Exists(path))
                {
                    current = new JavaScriptSerializer().Deserialize<LegacySnapshot>(File.ReadAllText(path));
                    if (current != null) Render(current);
                }
            }
            catch (Exception) { status.Text = "Прошлый отчёт недоступен; выполните новую проверку."; }
        }

        private void SaveReport(object sender, EventArgs args)
        {
            if (current == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "ITSeti-" + current.ComputerName + "-Win7.txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, current.ToReport(), new UTF8Encoding(true)); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить отчёт", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void OpenEngineer(object sender, EventArgs args)
        {
            try
            {
                if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    new MainForm(true).Show();
                else
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--engineer")
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Инженерное окно", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
