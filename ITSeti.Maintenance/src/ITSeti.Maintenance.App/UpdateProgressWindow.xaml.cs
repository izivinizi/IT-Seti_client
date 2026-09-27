using System.Text.RegularExpressions;
using System.Windows;

namespace ITSeti.Maintenance.App;

public partial class UpdateProgressWindow : Window
{
    private readonly Func<IProgress<string>?, Task<bool>> runUpdate;
    private bool operationFinished;

    public UpdateProgressWindow(string targetVersion, Func<IProgress<string>?, Task<bool>> runUpdate)
    {
        InitializeComponent();
        this.runUpdate = runUpdate;
        VersionLabel.Text = string.IsNullOrWhiteSpace(targetVersion)
            ? "Подготовка последнего выпуска GitHub"
            : $"Версия {targetVersion}";
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, args) =>
        {
            if (!operationFinished) args.Cancel = true;
        };
    }

    private async Task RunAsync()
    {
        var progress = new Progress<string>(SetStatus);
        try
        {
            if (await runUpdate(progress))
            {
                StatusLabel.Text = "Установщик загружен и проверен. Приложение закроется для установки.";
                HintLabel.Text = "После завершения откройте приложение снова.";
                await Task.Delay(1100);
                operationFinished = true;
                DialogResult = true;
                return;
            }

            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
            HintLabel.Text = "Приложение осталось открытым. Можно закрыть это окно и повторить попытку позже.";
            CloseButton.Visibility = Visibility.Visible;
            operationFinished = true;
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось обновить приложение: " + ex.Message);
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
            HintLabel.Text = "Приложение осталось открытым. Проверьте подключение и повторите попытку.";
            CloseButton.Visibility = Visibility.Visible;
            operationFinished = true;
        }
    }

    private void SetStatus(string message)
    {
        StatusLabel.Text = message;
        var match = Regex.Match(message, @"Скачивание установщика:\s*(?<percent>\d{1,3})%");
        if (!match.Success || !double.TryParse(match.Groups["percent"].Value, out var percent)) return;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = Math.Clamp(percent, 0, 100);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
