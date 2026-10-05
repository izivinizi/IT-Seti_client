using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ITSeti.Maintenance.App;

internal sealed class TicketMessagePoller : IDisposable
{
    private sealed record SavedState(string DeviceId, DateTimeOffset Cursor, Guid CursorId, Guid[] Seen);
    private readonly Window owner;
    private readonly Action openTickets;
    private readonly Func<ServerSupportClient?> clientFactory;
    private readonly Action<string>? notification;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "ticket-notifications.json");
    private readonly HashSet<Guid> seen = [];
    private DateTimeOffset? cursor;
    private Guid cursorId;
    private string? deviceId;
    private bool polling;
    private bool disposed;
    private System.Windows.Forms.NotifyIcon? tray;
    public bool HasActiveTickets { get; private set; }
    public bool ExitRequested { get; private set; }

    public TicketMessagePoller(Window owner, Action openTickets, Func<ServerSupportClient?>? clientFactory = null,
        Action<string>? notification = null, string? statePath = null)
    {
        this.owner = owner;
        this.openTickets = openTickets;
        this.clientFactory = clientFactory ?? ServerSupportClient.Open;
        this.notification = notification;
        if (statePath is not null) this.statePath = statePath;
        timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start() { if (!disposed) { timer.Start(); _ = PollAsync(); } }
    public void RefreshSoon() { if (!disposed) _ = PollAsync(); }

    internal async Task PollAsync()
    {
        if (polling || disposed) return;
        polling = true;
        try
        {
            using var client = clientFactory();
            if (client is null) return;
            if (deviceId != client.DeviceId)
            {
                deviceId = client.DeviceId; cursor = null; cursorId = Guid.Empty; seen.Clear();
                try
                {
                    if (new FileInfo(statePath).Length <= 128 * 1024)
                    {
                        var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(statePath));
                        if (saved?.DeviceId == deviceId && saved.Seen is not null && saved.Cursor <= DateTimeOffset.UtcNow.AddMinutes(2)) { cursor = saved.Cursor; cursorId = saved.CursorId; seen.UnionWith(saved.Seen.TakeLast(1000)); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            var path = "/api/v1/ticket-updates" + (cursor.HasValue ? "?since=" + Uri.EscapeDataString(cursor.Value.ToString("O")) + "&afterId=" + cursorId : "");
            using var response = await client.GetAsync(path, lifetime.Token);
            if (disposed) return;
            var root = response.RootElement;
            HasActiveTickets = root.GetProperty("activeCount").GetInt64() > 0;
            var incoming = root.GetProperty("messages").EnumerateArray().Where(m => !seen.Contains(m.GetProperty("id").GetGuid())).ToArray();
            if (notification is null && (HasActiveTickets || incoming.Length > 0)) EnsureTray();
            if (incoming.Length > 0)
            {
                var title = incoming[^1].GetProperty("title").GetString() ?? "Заявка";
                var text = incoming.Length == 1 ? title : $"Новых ответов: {incoming.Length}. {title}";
                if (notification is not null) notification(text);
                else tray?.ShowBalloonTip(10000, "ИТ-Сети · новый ответ", text, System.Windows.Forms.ToolTipIcon.Info);
            }
            foreach (var item in root.GetProperty("messages").EnumerateArray()) seen.Add(item.GetProperty("id").GetGuid());
            cursor = root.GetProperty("cursor").GetDateTimeOffset();
            cursorId = root.GetProperty("cursorId").GetGuid();
            if (seen.Count > 1000)
            {
                var recent = seen.TakeLast(1000).ToArray(); seen.Clear(); seen.UnionWith(recent);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedState(deviceId!, cursor.Value, cursorId, seen.ToArray())));
                File.Move(temporary, statePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            timer.Interval = TimeSpan.FromSeconds(root.GetProperty("hasMore").GetBoolean() ? 1 : HasActiveTickets ? 30 : 120);
            if (!HasActiveTickets && incoming.Length == 0 && owner.IsVisible) DisposeTray();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
        {
            // A failed poll never advances the cursor or interrupts the user's work.
            timer.Interval = TimeSpan.FromMinutes(2);
        }
        finally { polling = false; }
    }

    private void EnsureTray()
    {
        if (tray is not null) return;
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/favicon.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(resource);
        tray = new System.Windows.Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "ИТ-Сети · заявки", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть приложение", null, (_, _) => owner.Dispatcher.Invoke(() => { owner.Show(); owner.Activate(); }));
        menu.Items.Add("Мои заявки", null, (_, _) => owner.Dispatcher.Invoke(openTickets));
        menu.Items.Add("Выход", null, (_, _) => owner.Dispatcher.Invoke(() => { ExitRequested = true; owner.Close(); }));
        tray.ContextMenuStrip = menu;
        tray.BalloonTipClicked += (_, _) => owner.Dispatcher.Invoke(openTickets);
        tray.DoubleClick += (_, _) => owner.Dispatcher.Invoke(() => { owner.Show(); owner.Activate(); });
    }

    private void DisposeTray()
    {
        if (tray is not null) { var icon = tray.Icon; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); icon?.Dispose(); tray = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); timer.Stop();
        DisposeTray();
        lifetime.Dispose();
    }
}
