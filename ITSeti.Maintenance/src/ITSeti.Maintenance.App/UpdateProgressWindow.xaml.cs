using System.Text.RegularExpressions;
using System.Windows;

namespace ITSeti.Maintenance.App;

public partial class UpdateProgressWindow : Window
{
    private readonly Func<IProgress<string>?, CancellationToken, Task<bool>> runUpdate;
    private readonly CancellationTokenSource cancellation = new();
    private bool closed;
    private bool runCompleted;

    public UpdateProgressWindow(string targetVersion, Func<IProgress<string>?, CancellationToken, Task<bool>> runUpdate)
    {
        InitializeComponent();
        this.runUpdate = runUpdate;
        VersionLabel.Text = string.IsNullOrWhiteSpace(targetVersion)
            ? "Подготовка обновления с сервера"
            : $"Версия {targetVersion}";
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, _) =>
        {
            closed = true;
            if (!runCompleted) cancellation.Cancel();
        };
    }

    private async Task RunAsync()
    {
        var progress = new Progress<string>(SetStatus);
        try
        {
            if (await runUpdate(progress, cancellation.Token))
            {
                if (closed) return;
                StatusLabel.Text = "Установщик загружен и проверен. Приложение закроется для установки.";
                HintLabel.Text = "После установки приложение откроется автоматически.";
                await Task.Delay(500, cancellation.Token);
                DialogResult = true;
                return;
            }

            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
            HintLabel.Text = "Приложение осталось открытым. Можно закрыть это окно и повторить попытку позже.";
            CloseButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closed) return;
            SetStatus("Не удалось обновить приложение: " + ex.Message);
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
            HintLabel.Text = "Приложение осталось открытым. Проверьте подключение и повторите попытку.";
            CloseButton.Visibility = Visibility.Visible;
        }
        finally { runCompleted = true; cancellation.Dispose(); }
    }

    private void SetStatus(string message)
    {
        if (closed) return;
        StatusLabel.Text = message;
        var match = Regex.Match(message, @"Скачивание установщика:\s*(?<percent>\d{1,3})%");
        if (!match.Success || !double.TryParse(match.Groups["percent"].Value, out var percent)) return;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = Math.Clamp(percent, 0, 100);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
