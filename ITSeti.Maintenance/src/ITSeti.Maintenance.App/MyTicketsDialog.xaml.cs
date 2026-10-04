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
    private sealed record Ticket(Guid Id, string Title, string State, long? IssueId)
    {
        public string Label => $"{Title}\n{(IssueId.HasValue ? $"Okdesk #{IssueId}" : State)}";
    }
    private sealed record Message(string Header, string Content);
    private sealed record Attachment(Guid Id, string FileName);
    private sealed record OutgoingFile(string FileName, string ContentBase64);
    private readonly List<OutgoingFile> files = [];
    private int selectionRequest;
    private int refreshRequest;
    private List<Attachment> attachments = [];

    public MyTicketsDialog() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
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
                item.GetProperty("status").GetString() ?? "", item.GetProperty("issueId").ValueKind == JsonValueKind.Null
                    ? null : item.GetProperty("issueId").GetInt64())).ToArray();
            Tickets.SelectedItem = (Tickets.ItemsSource as Ticket[])?.FirstOrDefault(t => t.Id == selected);
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
        if (Tickets.SelectedItem is not Ticket ticket) { TicketHeader.Text = ""; return; }
        try
        {
            TicketHeader.Text = $"{ticket.Title} · {ticket.State}";
            using var client = ServerSupportClient.Open() ?? throw new InvalidOperationException("Компьютер не подключён к серверу.");
            using var data = await client.GetAsync($"/api/v1/tickets/{ticket.Id}/messages");
            if (request != selectionRequest) return;
            Messages.ItemsSource = data.RootElement.GetProperty("messages").EnumerateArray().Select(item => new Message(
                $"{(item.GetProperty("direction").GetString() == "engineer" ? "Поддержка" : "Пользователь")} · {item.GetProperty("createdAt").GetDateTime().ToLocalTime():dd.MM.yyyy HH:mm} · {item.GetProperty("status").GetString()}",
                PlainText(item.GetProperty("content").GetString() ?? ""))).ToArray();
            attachments = data.RootElement.GetProperty("attachments").EnumerateArray().Select(item => new Attachment(
                item.GetProperty("id").GetGuid(), item.GetProperty("fileName").GetString() ?? "файл")).ToList();
            AttachmentPicker.ItemsSource = attachments;
            AttachmentPicker.SelectedIndex = attachments.Count > 0 ? 0 : -1;
            StatusText.Text = "";
        }
        catch (Exception ex) { if (request == selectionRequest) StatusText.Text = ex.Message; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (Tickets.SelectedItem is not Ticket ticket || string.IsNullOrWhiteSpace(ReplyBody.Text)) return;
        SendButton.IsEnabled = false;
        Tickets.IsEnabled = false;
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
        finally { SendButton.IsEnabled = true; Tickets.IsEnabled = true; }
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
