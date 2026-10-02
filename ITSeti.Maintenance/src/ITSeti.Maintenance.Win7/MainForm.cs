using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ITSeti.Maintenance.Win7
{
    public sealed class MainForm : Form
    {
        private static readonly Color Blue = Color.FromArgb(23, 70, 150);
        private static readonly Color Ink = Color.FromArgb(23, 36, 52);
        private static readonly Color Muted = Color.FromArgb(82, 99, 118);
        private readonly Button checkButton = Command("Проверить компьютер", true);
        private readonly Button saveButton = Command("Сохранить отчёт", false);
        private readonly Button copyButton = Command("Копировать", false);
        private readonly Label heading = TextLabel("Диагностика ПК", 18, Blue);
        private readonly Label checkedAt = TextLabel("Проверок пока нет", 10, Muted);
        private readonly Label cpu = TextLabel("CPU  ·  —", 12, Ink);
        private readonly Label memory = TextLabel("ОЗУ  ·  —", 12, Ink);
        private readonly Label systemDisk = TextLabel("Диск C:  ·  —", 12, Ink);
        private readonly Label status = TextLabel("Подготовка проверки", 11, Blue);
        private readonly ListBox findings = new ListBox();
        private readonly RichTextBox report = new RichTextBox();
        private readonly string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "MaintenanceWin7");
        private LegacySnapshot current;
        private bool running;

        public MainForm()
        {
            Text = "ИТ-Сети Обслуживание ПК · Windows 7 beta";
            BackColor = Color.White;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(640, area.Width), Math.Min(530, area.Height));
            Size = new Size(Math.Min(1060, area.Width), Math.Min(790, area.Height));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 16), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            title.Controls.Add(heading, 0, 0);
            title.Controls.Add(checkedAt, 0, 1);
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
            actions.Controls.Add(checkButton);
            actions.Controls.Add(saveButton);
            actions.Controls.Add(copyButton);
            layout.Controls.Add(actions, 0, 2);
            checkButton.Click += async (sender, args) => await CheckAsync();
            saveButton.Click += SaveReport;
            copyButton.Click += (sender, args) => { if (current != null) Clipboard.SetText(current.ToReport()); };

            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 3);
            layout.Controls.Add(TextLabel("Что требует внимания", 12, Blue), 0, 4);

            findings.Dock = DockStyle.Fill;
            findings.BorderStyle = BorderStyle.FixedSingle;
            findings.Font = Font;
            findings.HorizontalScrollbar = true;
            layout.Controls.Add(findings, 0, 5);
            layout.Controls.Add(TextLabel("Подробности проверки", 12, Blue), 0, 6);

            report.Dock = DockStyle.Fill;
            report.BorderStyle = BorderStyle.FixedSingle;
            report.BackColor = Color.White;
            report.ForeColor = Ink;
            report.Font = new Font("Segoe UI", 10);
            report.ReadOnly = true;
            report.DetectUrls = false;
            layout.Controls.Add(report, 0, 7);

            LoadCachedReport();
            Shown += async (sender, args) => await CheckAsync();
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

        private async Task CheckAsync()
        {
            if (running) return;
            running = true;
            checkButton.Enabled = false;
            status.Text = "Идёт проверка. Работать за компьютером можно как обычно.";
            status.ForeColor = Blue;
            try
            {
                var snapshot = await Task.Run(() => LegacyDiagnostics.Collect());
                current = snapshot;
                Render(snapshot);
                try
                {
                    Directory.CreateDirectory(dataPath);
                    File.WriteAllText(Path.Combine(dataPath, "latest.json"), snapshot.ToJson(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(dataPath, "latest.txt"), snapshot.ToReport(), new UTF8Encoding(false));
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
            }
        }

        private void Render(LegacySnapshot snapshot)
        {
            checkedAt.Text = snapshot.ComputerName + "  ·  проверено " + snapshot.CheckedAt;
            cpu.Text = "CPU  ·  " + (snapshot.CpuPercent.HasValue ? snapshot.CpuPercent.Value.ToString("N0") + "%" : "нет данных");
            memory.Text = "ОЗУ  ·  " + (snapshot.TotalMemoryGb.HasValue ? string.Format("{0:N1} ГБ свободно", snapshot.FreeMemoryGb) : "нет данных");
            var drive = snapshot.Disks.Find(x => x.IsSystem);
            systemDisk.Text = "Системный диск  ·  " + (drive == null ? "нет данных" : string.Format("{0:N1} ГБ свободно", drive.FreeBytes / 1073741824.0));
            findings.Items.Clear();
            foreach (var item in snapshot.Findings) findings.Items.Add(item);
            if (snapshot.Findings.Count == 0) findings.Items.Add(snapshot.Unavailable.Count == 0 ? "По доступным данным замечаний нет." : "Есть недоступные показатели; смотрите подробности ниже.");
            report.Text = snapshot.ToReport();
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
    }
}
