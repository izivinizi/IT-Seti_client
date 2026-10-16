using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ITSeti.Maintenance.App;

public partial class ServerAssignmentDialog : Window
{
    private readonly ObservableCollection<ServerCatalogCompany> companies = [];
    private readonly ObservableCollection<ServerCatalogSite> sites = [];
    private List<ServerCatalogCompany> allCompanies = [];
    private List<ServerCatalogSite> allSites = [];
    private ServerAssignment? current;
    private ServerEnrollmentClient? client;
    private bool updating;
    private bool busy;

    public ServerAssignmentDialog()
    {
        InitializeComponent();
        CompanyBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(CompanyBox_TextChanged));
        SiteBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(SiteBox_TextChanged));
        CompanyBox.DropDownOpened += (_, _) => ExpandChoices(CompanyBox, () => RefreshCompanies(null));
        SiteBox.DropDownOpened += (_, _) => ExpandChoices(SiteBox, () => RefreshSites());
        Loaded += LoadAsync;
    }

    private async void LoadAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadAsync;
        SetBusy(true, "Подключаемся к серверу и получаем актуальный каталог…");
        try
        {
            client = new ServerEnrollmentClient();
            var result = await client.LoadAsync();
            current = result.Assignment;
            allCompanies = result.Companies.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            allSites = result.Sites.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            CompanyBox.ItemsSource = companies;
            SiteBox.ItemsSource = sites;
            updating = true;
            RefreshCompanies(null);
            CompanyBox.SelectedItem = companies.FirstOrDefault(item => item.Id == current.CompanyId);
            RefreshSites();
            if (current.SiteId is { } siteId && sites.FirstOrDefault(item => item.Id == siteId) is { } selectedSite)
                SiteBox.SelectedItem = selectedSite;
            updating = false;
            SetBusy(false, current.CompanyId > 0
                ? "Сервер подключён. Поиск выполняется прямо в поле списка."
                : "Выберите компанию. Если у неё один объект, он будет выбран автоматически.");
            UpdateSaveState();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException)
        {
            SetBusy(false, ex.Message);
            SaveButton.IsEnabled = false;
        }
    }

    private void RefreshCompanies(string? query)
    {
        var value = query?.Trim() ?? "";
        companies.Clear();
        foreach (var item in allCompanies.Where(item => value.Length == 0 || item.Name.Contains(value, StringComparison.CurrentCultureIgnoreCase))) companies.Add(item);
    }

    private void ExpandChoices(ComboBox box, Action refresh)
    {
        if (updating || busy || box.SelectedItem is not { } selected) return;
        updating = true;
        try { refresh(); box.SelectedItem = selected; }
        finally { updating = false; }
        UpdateSaveState();
    }

    private void RefreshSites(string? query = null)
    {
        var company = CompanyBox.SelectedItem as ServerCatalogCompany;
        var value = query ?? "";
        sites.Clear();
        if (company is null) return;
        sites.Add(new ServerCatalogSite(0, "Без объекта", null, company.Id));
        foreach (var item in allSites.Where(item => item.CompanyId == company.Id && (value.Length == 0 || (item.Name + " " + item.Address).Contains(value, StringComparison.CurrentCultureIgnoreCase)))) sites.Add(item);
        if (query is null)
        {
            if (sites.Count == 2) SiteBox.SelectedIndex = 1;
            else if (SiteBox.SelectedItem is null) SiteBox.SelectedIndex = 0;
        }
    }

    private void CompanyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating) return;
        var query = CompanyBox.Text;
        if (CompanyBox.SelectedItem is ServerCatalogCompany selected && query == selected.Name)
        {
            UpdateSaveState();
            return;
        }
        updating = true;
        RefreshCompanies(query);
        RefreshSites();
        SiteBox.IsEnabled = false;
        CompanyBox.Text = query;
        MoveCaretToEnd(CompanyBox);
        CompanyBox.IsDropDownOpen = true;
        updating = false;
        UpdateSaveState();
    }

    private void CompanyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        updating = true;
        RefreshSites();
        SiteBox.IsEnabled = CompanyBox.SelectedItem is not null;
        updating = false;
        UpdateSaveState();
    }

    private void SiteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || CompanyBox.SelectedItem is not ServerCatalogCompany) return;
        var query = SiteBox.Text;
        if (SiteBox.SelectedItem is ServerCatalogSite selected && query == selected.DisplayName)
        {
            UpdateSaveState();
            return;
        }
        updating = true;
        RefreshSites(query);
        SiteBox.Text = query;
        MoveCaretToEnd(SiteBox);
        SiteBox.IsDropDownOpen = true;
        updating = false;
        UpdateSaveState();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || CompanyBox.SelectedItem is not ServerCatalogCompany company) return;
        var site = SiteBox.SelectedItem as ServerCatalogSite;
        SetBusy(true, "Сохраняем привязку компьютера…");
        try
        {
            await client.SaveAssignmentAsync(company.Id, site is { Id: > 0 } ? site.Id : null, null);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            SetBusy(false, ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void SiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating) UpdateSaveState();
    }
    private void UpdateSaveState() => SaveButton.IsEnabled = !busy && client is not null
        && CompanyBox.SelectedItem is ServerCatalogCompany company
        && SiteBox.SelectedItem is ServerCatalogSite site && site.CompanyId == company.Id;
    private void SetBusy(bool busy, string message)
    {
        this.busy = busy;
        StatusText.Text = message;
        CompanyBox.IsEnabled = !busy;
        SiteBox.IsEnabled = !busy && CompanyBox.SelectedItem is not null;
        UpdateSaveState();
    }

    private static void MoveCaretToEnd(ComboBox comboBox)
    {
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox editor)
            editor.CaretIndex = editor.Text.Length;
    }

    protected override void OnClosed(EventArgs e) { client?.Dispose(); base.OnClosed(e); }
}
