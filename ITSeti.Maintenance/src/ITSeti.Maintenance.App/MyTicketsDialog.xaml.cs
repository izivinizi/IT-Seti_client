using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ITSeti.Maintenance.App;

public partial class MyTicketsDialog : Window
{
    private sealed record Ticket(Guid Id, string Title, string State, long? IssueId, string Description, DateTime? CreatedAt)
    {
        public string StateLabel => State switch
        {
            "queued" => "Принята · ожидает отправки в Okdesk",
            "sent" or "created" => "Передана в Okdesk",
            "open" => "В работе",
            "sending" => "Отправляется в Okdesk",
            "rejected" => "Принята · Okdesk отклонил отправку",
            "unknown" => "Принята · отправка в Okdesk не подтверждена",
            "completed" or "resolved" or "closed" => "Выполнена",
            "cancelled" or "canceled" => "Отменена",
            "failed" => "Принята · ошибка отправки в Okdesk",
            _ => "Принята"
        };
    }
    private sealed record Message(string Author, string Date, string Content, bool IsEngineer);
    private sealed record Attachment(Guid Id, string FileName);
    private sealed record OutgoingFile(string FileName, string ContentBase64);
    private readonly List<OutgoingFile> files = [];
    private int selectionRequest;
    private int refreshRequest;
    private Guid? draftTicket;
    private List<Attachment> attachments = [];
    private readonly System.Windows.Threading.DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MyTicketsDialog()
    {
        InitializeComponent();
        refreshTimer.Tick += async (_, _) => { if (Tickets.IsEnabled) await RefreshAsync(); };
        Closed += (_, _) => { isClosed = true; refreshTimer.Stop(); refreshRequest++; selectionRequest++; };
    }

    private bool isClosed;
    private async void Window_Loaded(object sender, RoutedEventArgs e) { await RefreshAsync(); if (!isClosed) refreshTimer.Start(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var request = ++refreshRequest;
        try
        {
            using var client = ServerSupportClient.Open() ?? throw new InvalidOperationException("Компьютер не подключён к серверу.");
            using var data = await client.GetAsync("/api/v1/tickets");
            if (request != refreshRequest) return;
            var selected = (Tickets.SelectedItem as Ticket)?.Id;
            Tickets.ItemsSource = data.RootElement.EnumerateArray().Select(item => new Ticket(
                item.GetProperty("requestId").GetGuid(), item.GetProperty("title").GetString() ?? "",
                item.TryGetProperty("workflowState", out var workflow) && workflow.GetString() is "completed" or "cancelled"
                    ? workflow.GetString()! : item.GetProperty("status").GetString() ?? "", item.GetProperty("issueId").ValueKind == JsonValueKind.Null
                    ? null : item.GetProperty("issueId").GetInt64(),
                item.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                item.TryGetProperty("createdAt", out var created) ? created.GetDateTime() : null)).ToArray();
            var tickets = (Ticket[])Tickets.ItemsSource;
            EmptyTickets.Visibility = tickets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Tickets.SelectedItem = tickets.FirstOrDefault(t => t.Id == selected) ?? tickets.FirstOrDefault();
            StatusText.Text = "";
        }
        catch (Exception ex) { if (request == refreshRequest) StatusText.Text = ex.Message; }
    }

    private async void Tickets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var request = ++selectionRequest;
        Messages.ItemsSource = null;
        attachments.Clear();
        AttachmentPicker.ItemsSource = null;
        ReceivedFiles.Visibility = Visibility.Collapsed;
        ReplyBody.IsEnabled = AttachButton.IsEnabled = SendButton.IsEnabled = Tickets.SelectedItem is Ticket;
        if (Tickets.SelectedItem is not Ticket ticket) { TicketHeader.Text = "Выберите заявку"; TicketState.Text = ""; return; }
        if (draftTicket != ticket.Id)
        {
            ReplyBody.Clear(); files.Clear(); AttachmentLabel.Text = "";
            draftTicket = ticket.Id;
        }
        try
        {
            TicketHeader.Text = ticket.Title;
            TicketState.Text = ticket.StateLabel + (ticket.IssueId.HasValue ? $" · Okdesk #{ticket.IssueId}" : "");
            using var client = ServerSupportClient.Open() ?? throw new InvalidOperationException("Компьютер не подключён к серверу.");
            using var data = await client.GetAsync($"/api/v1/tickets/{ticket.Id}/messages");
            if (request != selectionRequest) return;
            var messages = data.RootElement.GetProperty("messages").EnumerateArray().Select(item => new Message(
                item.GetProperty("direction").GetString() == "engineer" ? "Поддержка" : "Вы",
                item.GetProperty("createdAt").GetDateTime().ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                PlainText(item.GetProperty("content").GetString() ?? ""),
                item.GetProperty("direction").GetString() == "engineer")).ToArray();
            Messages.ItemsSource = string.IsNullOrWhiteSpace(ticket.Description) ? messages :
                messages.Prepend(new Message("Вы · заявка", ticket.CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "",
                    PlainText(ticket.Description), false)).ToArray();
            if (Messages.Items.Count > 0) Messages.ScrollIntoView(Messages.Items[Messages.Items.Count - 1]);
            attachments = data.RootElement.GetProperty("attachments").EnumerateArray().Select(item => new Attachment(
                item.GetProperty("id").GetGuid(), item.GetProperty("fileName").GetString() ?? "файл")).ToList();
            AttachmentPicker.ItemsSource = attachments;
            AttachmentPicker.SelectedIndex = attachments.Count > 0 ? 0 : -1;
            ReceivedFiles.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = "";
        }
        catch (Exception ex) { if (request == selectionRequest) StatusText.Text = ex.Message; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (Tickets.SelectedItem is not Ticket ticket || string.IsNullOrWhiteSpace(ReplyBody.Text)) return;
        SendButton.IsEnabled = false;
        Tickets.IsEnabled = false;
        RefreshButton.IsEnabled = ReplyBody.IsEnabled = AttachButton.IsEnabled = false;
        try
        {
            using var client = ServerSupportClient.Open()!;
            using var _ = await client.PostAsync($"/api/v1/tickets/{ticket.Id}/messages", new {
                messageId = Guid.NewGuid(), content = ReplyBody.Text.Trim(),
                attachments = files.Select(f => new { f.FileName, f.ContentBase64 }).ToArray()
            });
            ReplyBody.Clear(); files.Clear(); AttachmentLabel.Text = "";
            Tickets_SelectionChanged(this, null!);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            Tickets.IsEnabled = RefreshButton.IsEnabled = true;
            SendButton.IsEnabled = ReplyBody.IsEnabled = AttachButton.IsEnabled = Tickets.SelectedItem is Ticket;
        }
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Файлы|*.png;*.jpg;*.jpeg;*.pdf;*.txt;*.log;*.docx;*.xlsx", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
            try
            {
                if (new FileInfo(path).Length > 5 * 1024 * 1024)
                    throw new InvalidOperationException("Размер вложения не должен превышать 5 МБ.");
                AddFile(Path.GetFileName(path), File.ReadAllBytes(path));
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void ReplyBody_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !Clipboard.ContainsImage()) return;
        try
        {
            var image = Clipboard.GetImage();
            if (image is null) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream(); encoder.Save(output);
            AddFile($"Снимок-{DateTime.Now:yyyyMMdd-HHmmss}.png", output.ToArray());
            e.Handled = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; e.Handled = true; }
    }

    private void AddFile(string name, byte[] bytes)
    {
        if (files.Count >= 3 || bytes.Length is < 1 or > 5 * 1024 * 1024 ||
            files.Sum(f => f.ContentBase64.Length * 3L / 4) + bytes.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("До 3 вложений, каждое до 5 МБ, всего до 10 МБ.");
        files.Add(new(name, Convert.ToBase64String(bytes)));
        AttachmentLabel.Text = string.Join(", ", files.Select(f => f.FileName));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Tickets.SelectedItem is not Ticket ticket || attachments.Count == 0) return;
        if (AttachmentPicker.SelectedItem is not Attachment file) return;
        var dialog = new SaveFileDialog { FileName = file.FileName };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var client = ServerSupportClient.Open()!;
            File.WriteAllBytes(dialog.FileName, await client.DownloadAsync($"/api/v1/tickets/{ticket.Id}/attachments/{file.Id}"));
            StatusText.Text = "Вложение сохранено.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
}
