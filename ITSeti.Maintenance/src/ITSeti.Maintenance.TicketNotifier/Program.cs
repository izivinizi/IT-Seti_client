using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace ITSeti.Maintenance.TicketNotifier;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ITSeti-Maintenance-TicketNotifier-" + Environment.UserName, out var created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        try { Application.Run(new TicketNotificationContext()); }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }
}

internal sealed class TicketNotificationContext : ApplicationContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string sharedStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "Service", "ticket-updates.json");
    private readonly string seenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "ticket-notifications.json");
    private readonly NotifyIcon icon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private HashSet<Guid> seen = [];
    private string? deviceId;

    public TicketNotificationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть ИТ-Сети", null, (_, _) => OpenApplication());
        menu.Items.Add("Уведомления о заявках работают", null, (_, _) => { }).Enabled = false;
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ИТ-Сети · уведомления заявок",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => OpenApplication();
        icon.BalloonTipClicked += (_, _) => OpenApplication();
        timer.Tick += (_, _) => ReadUpdates();
        ReadSeenState();
        timer.Start();
        ReadUpdates();
    }

    private void ReadSeenState()
    {
        try
        {
            if (!File.Exists(seenPath) || new FileInfo(seenPath).Length > 64 * 1024) return;
            var state = JsonSerializer.Deserialize<SeenState>(File.ReadAllText(seenPath), JsonOptions);
            if (state?.Seen is not null)
            {
                deviceId = state.DeviceId;
                seen.UnionWith(state.Seen.TakeLast(1000));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void ReadUpdates()
    {
        try
        {
            if (!File.Exists(sharedStatePath) || new FileInfo(sharedStatePath).Length > 512 * 1024) return;
            var state = JsonSerializer.Deserialize<TicketUpdateState>(File.ReadAllText(sharedStatePath), JsonOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.DeviceId)) return;
            if (!string.Equals(deviceId, state.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                deviceId = state.DeviceId;
                seen.Clear();
            }

            var incoming = (state.Messages ?? []).Where(message => message is not null && !seen.Contains(message.Id)).ToArray();
            if (incoming.Length == 0) return;
            foreach (var item in incoming) seen.Add(item.Id);
            if (seen.Count > 1000) seen = seen.TakeLast(1000).ToHashSet();
            SaveSeenState();

            var lastTitle = incoming[^1].Title;
            var title = incoming.Length == 1 ? "Новый ответ по заявке" : $"Новых ответов по заявкам: {incoming.Length}";
            var message = incoming.Length == 1 ? lastTitle : $"Последний ответ: {lastTitle}";
            icon.ShowBalloonTip(10000, title, message, ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
    }

    private void SaveSeenState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(seenPath)!);
        var temporary = seenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SeenState(deviceId!, seen.ToArray()), JsonOptions));
            File.Move(temporary, seenPath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static void OpenApplication()
    {
        var appPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ITSeti.Maintenance.exe"));
        if (!File.Exists(appPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(appPath)! });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            icon.Visible = false;
            icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record SeenState(string DeviceId, Guid[] Seen);
    private sealed record TicketUpdateState(string DeviceId, DateTimeOffset? Cursor, Guid CursorId, int ActiveCount,
        DateTimeOffset UpdatedAtUtc, TicketUpdateMessage[]? Messages);
    private sealed record TicketUpdateMessage(Guid Id, string Title, DateTimeOffset CreatedAt);
}
