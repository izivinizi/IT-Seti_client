using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace ITSeti.Maintenance.App;

public partial class ContactsDialog : Window
{
    public ContactsDialog(string identity)
    {
        InitializeComponent();
        IdentityValue.Text = identity;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        MaxWidth = SystemParameters.WorkArea.Width - 24;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardHelper.Copy(IdentityValue.Text); CopyStatus.Text = "Данные скопированы"; }
        catch (Exception ex) { CopyStatus.Text = ex.Message; }
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { CopyStatus.Text = ex.Message; }
        e.Handled = true;
    }
}
