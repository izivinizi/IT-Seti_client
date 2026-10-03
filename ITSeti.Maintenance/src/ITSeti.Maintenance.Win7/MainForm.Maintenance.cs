using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITSeti.Maintenance.Win7
{
    public sealed partial class MainForm
    {
        private readonly Label maintenanceStatus = TextLabel("Действия не запускались", 10, Ink);

        private void BuildMaintenanceTab(TabControl tabs)
        {
            var page = Page("Обслуживание", tabs);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
            page.Controls.Add(scroll);
            var content = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(0), Width = 880 };
            scroll.Controls.Add(content);
            content.Controls.Add(MaintenanceHeading("Обслуживание Windows 7", 15, 40));

            var common = new FlowLayoutPanel { Height = 52, Width = 810, WrapContents = false };
            var check = Command("Проверить обновления", false); check.Width = 205;
            check.Click += async (sender, args) => await RunMaintenanceAsync("Поиск обновлений Windows…", LegacyMaintenance.CheckWindowsUpdates);
            common.Controls.Add(check);
            if (!engineerMode)
            {
                var cleanup = Command("Очистить мои временные файлы", false); cleanup.Width = 240;
                cleanup.Click += async (sender, args) => await RunMaintenanceAsync("Очистка файлов текущего пользователя…", LegacyMaintenance.CleanCurrentUser);
                common.Controls.Add(cleanup);
            }
            content.Controls.Add(common);

            if (engineerMode)
            {
                content.Controls.Add(MaintenanceHeading("Системные приложения", 12, 34));
                var tools = new FlowLayoutPanel { Height = 108, Width = 810, WrapContents = true };
                foreach (var tool in new[]
                {
                    new[] { "Управление компьютером", "compmgmt.msc" },
                    new[] { "Групповые политики", "gpedit.msc" },
                    new[] { "Терминал", "cmd.exe" },
                    new[] { "Панель управления", "control.exe" },
                    new[] { "Программы", "appwiz.cpl" },
                    new[] { "Сетевые адаптеры", "ncpa.cpl" },
                    new[] { "Сведения о системе", "msinfo32.exe" }
                })
                {
                    var title = tool[0]; var file = tool[1];
                    var button = Command(title, false); button.Width = 185;
                    button.Click += (sender, args) => LaunchSystemTool(file);
                    tools.Controls.Add(button);
                }
                content.Controls.Add(tools);
                content.Controls.Add(MaintenanceHeading("Обновления Windows", 12, 34));
                var policies = new FlowLayoutPanel { Height = 54, Width = 810, WrapContents = false };
                var disable = Command("Отключить автообновления", false); disable.Width = 235;
                disable.Click += async (sender, args) => await RunMaintenanceAsync("Отключение автоматической установки…", LegacyMaintenance.DisableAutomaticUpdates);
                var restore = Command("Вернуть прежнюю настройку", false); restore.Width = 235;
                restore.Click += async (sender, args) => await RunMaintenanceAsync("Восстановление политики обновлений…", LegacyMaintenance.RestoreAutomaticUpdates);
                policies.Controls.Add(disable); policies.Controls.Add(restore);
                content.Controls.Add(policies);
                content.Controls.Add(MaintenanceHeading("Восстановление Windows", 12, 34));
                var repair = Command("Запустить SFC", true); repair.Width = 165;
                repair.Click += async (sender, args) => await RunMaintenanceAsync("SFC проверяет системные файлы…", LegacyMaintenance.RunSfc);
                content.Controls.Add(repair);
            }
            maintenanceStatus.Width = 810;
            maintenanceStatus.Height = 90;
            maintenanceStatus.AutoEllipsis = false;
            content.Controls.Add(maintenanceStatus);
        }

        private static Label MaintenanceHeading(string title, int size, int height)
        {
            var label = TextLabel(title, size, Blue);
            label.Dock = DockStyle.None;
            label.Width = 810;
            label.Height = height;
            return label;
        }

        private async Task RunMaintenanceAsync(string progress, Func<string> action)
        {
            maintenanceStatus.Text = progress;
            maintenanceStatus.ForeColor = Blue;
            try
            {
                maintenanceStatus.Text = await Task.Run(action);
                maintenanceStatus.ForeColor = Color.FromArgb(24, 121, 83);
            }
            catch (Exception ex)
            {
                maintenanceStatus.Text = "Не выполнено: " + ex.Message;
                maintenanceStatus.ForeColor = Color.DarkRed;
            }
        }

        private void LaunchSystemTool(string file)
        {
            if (!engineerMode) return;
            try
            {
                var executable = file.EndsWith(".msc", StringComparison.OrdinalIgnoreCase) ? "mmc.exe" :
                    file.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) ? "control.exe" : file;
                var arguments = executable == "mmc.exe" || executable == "control.exe" && file != "control.exe" ? file : "";
                Process.Start(new ProcessStartInfo(executable, arguments)
                { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory });
            }
            catch (Exception ex) { maintenanceStatus.Text = "Не удалось открыть " + file + ": " + ex.Message; }
        }
    }
}
