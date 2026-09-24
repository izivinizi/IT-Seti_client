using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace ITSeti.Maintenance.App;

public partial class SupportDialog : Window
{
    public SupportDialog() => InitializeComponent();

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть ссылку", MessageBoxButton.OK, MessageBoxImage.Warning); }
        e.Handled = true;
    }

    private void CopyEmail_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardHelper.Copy("support@it-seti.ru"); CopyStatus.Text = "Адрес скопирован"; }
        catch (Exception ex) { CopyStatus.Text = $"Не удалось скопировать: {ex.Message}"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
