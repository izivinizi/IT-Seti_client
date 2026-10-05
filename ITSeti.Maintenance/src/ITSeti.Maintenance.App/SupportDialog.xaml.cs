using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace ITSeti.Maintenance.App;

public partial class SupportDialog : Window
{
    private readonly List<SupportFile> files = [];
    private readonly string identity;
    private string? pendingPayload;
    private Guid pendingRequestId;
    private readonly string contactPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ITSeti", "Maintenance", "support-contact.txt");
    private sealed record Service(string Code, string Name);
    private sealed record SupportFile(string FileName, string ContentBase64);

    public SupportDialog() : this(null, null, null) { }

    public SupportDialog(string? inventoryNumber, string? rmsId, string? anyDeskId)
    {
        InitializeComponent();
        Width = Math.Min(680, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(640, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(540, Width);
        MinHeight = Math.Min(480, Height);
        MaxHeight = SystemParameters.WorkArea.Height - 16;
        identity = $"Инв. номер: {inventoryNumber ?? "не указан"}{Environment.NewLine}RMS: {rmsId ?? "не найден"}{Environment.NewLine}AnyDesk: {anyDeskId ?? "не найден"}";
        TicketService.ItemsSource = new[] {
            new Service("517", "1С"), new Service("518", "Сетевая инфраструктура"),
            new Service("519", "Серверная инфраструктура"), new Service("520", "АТС"),
            new Service("521", "Видеонаблюдение"), new Service("522", "Удалённый доступ"),
            new Service("523", "Почта"), new Service("524", "Права доступа"),
            new Service("525", "Другое"), new Service("615", "ЭЦП") };
        TicketService.SelectedIndex = 8;
        try { if (File.Exists(contactPath)) TicketContact.Text = File.ReadAllText(contactPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { TicketStatus.Text = "Не удалось загрузить сохранённый контакт. Его можно указать заново."; }
    }

    private void Contacts_Click(object sender, RoutedEventArgs e) =>
        new ContactsDialog(identity) { Owner = this }.ShowDialog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void SubmitTicket_Click(object sender, RoutedEventArgs e)
    {
        if (TicketTitle.Text.Trim().Length < 3 || string.IsNullOrWhiteSpace(TicketBody.Text) || TicketService.SelectedItem is not Service service)
        { TicketStatus.Text = "Введите тему, сервис и суть заявки."; return; }
        SubmitTicket.IsEnabled = false;
        Composer.IsEnabled = false;
        TicketStatus.Text = "Отправляю заявку…";
        try
        {
            using var client = ServerSupportClient.Open() ?? throw new InvalidOperationException(
                "Компьютер ещё не подключён к серверу. Укажите компанию и объект в режиме инженера.");
            var contact = TicketContact.Text.Trim();
            var contactSaved = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(contactPath)!);
                File.WriteAllText(contactPath, contact);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { contactSaved = false; }
            var description = TicketBody.Text.Trim();
            if (contact.Length > 0) description += $"\n\nКонтакт: {contact}";
            if (IncludePcInfo.IsChecked == true) description += $"\n\nДанные ПК:\n{identity}";
            var payload = JsonSerializer.Serialize(new { title = TicketTitle.Text.Trim(), service.Code, description, files });
            if (payload != pendingPayload) { pendingPayload = payload; pendingRequestId = Guid.NewGuid(); }
            using var response = await client.PostAsync("/api/v1/tickets", new {
                requestId = pendingRequestId, title = TicketTitle.Text.Trim(), serviceCode = service.Code,
                description, attachments = files.Select(f => new { f.FileName, f.ContentBase64 }).ToArray()
            });
            TicketStatus.Text = "Заявка сохранена на сервере. Статус отправки в Okdesk виден в «Мои заявки».";
            if (!contactSaved) TicketStatus.Text += " Контакт не удалось запомнить на этом ПК.";
            pendingPayload = null;
            TicketTitle.Clear(); TicketBody.Clear(); files.Clear(); RefreshFiles();
        }
        catch (Exception ex) { TicketStatus.Text = ex.Message; }
        finally { SubmitTicket.IsEnabled = true; Composer.IsEnabled = true; }
    }

    private void MyTickets_Click(object sender, RoutedEventArgs e)
    {
        new MyTicketsDialog { Owner = this }.ShowDialog();
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Файлы|*.png;*.jpg;*.jpeg;*.pdf;*.txt;*.log;*.docx;*.xlsx", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
        {
            try
            {
                if (new FileInfo(path).Length > 5 * 1024 * 1024)
                    throw new InvalidOperationException("Размер вложения не должен превышать 5 МБ.");
                AddFile(Path.GetFileName(path), File.ReadAllBytes(path));
            }
            catch (Exception ex) { TicketStatus.Text = ex.Message; }
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (TicketFiles.SelectedIndex < 0 || TicketFiles.SelectedIndex >= files.Count) return;
        files.RemoveAt(TicketFiles.SelectedIndex);
        RefreshFiles();
    }

    private void TicketBody_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !Clipboard.ContainsImage()) return;
        try
        {
            var image = Clipboard.GetImage();
            if (image is null) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            AddFile($"Снимок-{DateTime.Now:yyyyMMdd-HHmmss}.png", stream.ToArray());
            e.Handled = true;
        }
        catch (Exception ex) { TicketStatus.Text = ex.Message; e.Handled = true; }
    }

    private void AddFile(string name, byte[] bytes)
    {
        if (files.Count >= 3 || bytes.Length is < 1 or > 5 * 1024 * 1024 ||
            files.Sum(f => f.ContentBase64.Length * 3L / 4) + bytes.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("До 3 вложений, каждое до 5 МБ, всего до 10 МБ.");
        files.Add(new(name, Convert.ToBase64String(bytes)));
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        TicketFiles.ItemsSource = files.Select(f => f.FileName).ToArray();
        TicketFiles.SelectedIndex = files.Count - 1;
        RemoveAttachment.IsEnabled = files.Count > 0;
    }
}
