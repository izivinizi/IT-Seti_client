using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class TicketUpdateMonitor : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string dataRoot;
    private readonly string supportPath;
    private readonly string statePath;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public string StatePath => statePath;

    public TicketUpdateMonitor(string? dataRoot = null, HttpMessageHandler? handler = null)
    {
        this.dataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        supportPath = Path.Combine(this.dataRoot, "server-support.json");
        statePath = Path.Combine(this.dataRoot, "Service", "ticket-updates.json");
        http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        ownsClient = true;
    }

    public async Task<TimeSpan> PollAsync(CancellationToken cancellationToken = default)
    {
        var config = ReadSupportConfiguration();
        if (config is null) return TimeSpan.FromSeconds(30);

        var state = ReadState(config.DeviceId);
        var url = new Uri(config.ServerUrl, "api/v1/ticket-updates");
        if (state.Cursor is { } cursor)
        {
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            url = new Uri(url + separator + "since=" + Uri.EscapeDataString(cursor.ToString("O", CultureInfo.InvariantCulture)) + "&afterId=" + state.CursorId.ToString("D"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Device-Id", config.DeviceId);
        request.Headers.Add("X-Support-Key", config.SupportKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Сервер заявок ответил HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("activeCount", out _) ||
            !root.TryGetProperty("hasMore", out _) || !root.TryGetProperty("cursor", out _) ||
            !root.TryGetProperty("cursorId", out _) || !root.TryGetProperty("messages", out _))
            throw new JsonException("Сервер вернул неполный ответ синхронизации заявок.");
        var activeCount = root.GetProperty("activeCount").GetInt32();
        var hasMore = root.GetProperty("hasMore").GetBoolean();
        var cursorValue = root.GetProperty("cursor").GetDateTimeOffset();
        var cursorId = root.GetProperty("cursorId").GetGuid();
        var messages = (state.Messages ?? []).TakeLast(500).ToList();
        var knownIds = messages.Select(message => message.Id).ToHashSet();

        if (state.Cursor is not null)
        {
            foreach (var item in root.GetProperty("messages").EnumerateArray())
            {
                var id = item.GetProperty("id").GetGuid();
                if (!knownIds.Add(id)) continue;
                var title = item.GetProperty("title").GetString();
                messages.Add(new TicketUpdateMessage(id, string.IsNullOrWhiteSpace(title) ? "Заявка" : title.Trim(),
                    item.GetProperty("createdAt").GetDateTimeOffset()));
            }
        }

        var next = new TicketUpdateState(config.DeviceId, cursorValue, cursorId, activeCount,
            DateTimeOffset.UtcNow, messages.TakeLast(500).ToArray());
        await WriteStateAsync(next, cancellationToken);
        if (hasMore) return TimeSpan.FromSeconds(1);
        return activeCount > 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);
    }

    private TicketSupportConfiguration? ReadSupportConfiguration()
    {
        try
        {
            if (!File.Exists(supportPath) || new FileInfo(supportPath).Length > 16 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(supportPath));
            var root = document.RootElement;
            var rawUrl = root.GetProperty("serverUrl").GetString();
            var deviceId = root.GetProperty("deviceId").GetString();
            var supportKey = root.GetProperty("supportKey").GetString();
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
                !server.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment) ||
                !Guid.TryParse(deviceId, out _) || string.IsNullOrWhiteSpace(supportKey) || supportKey.Length > 512)
                return null;
            return new TicketSupportConfiguration(new Uri(server.GetLeftPart(UriPartial.Authority) + "/"), deviceId!, supportKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private TicketUpdateState ReadState(string deviceId)
    {
        try
        {
            if (new FileInfo(statePath).Length > 512 * 1024) return TicketUpdateState.Empty(deviceId);
            var state = JsonSerializer.Deserialize<TicketUpdateState>(File.ReadAllText(statePath), JsonOptions);
            return state?.DeviceId == deviceId && state.Messages is not null
                ? state with { Messages = state.Messages.Where(message => message is not null && message.Id != Guid.Empty).TakeLast(500).ToArray() }
                : TicketUpdateState.Empty(deviceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return TicketUpdateState.Empty(deviceId);
        }
    }

    private async Task WriteStateAsync(TicketUpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
            File.Move(temporary, statePath, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }

    private sealed record TicketSupportConfiguration(Uri ServerUrl, string DeviceId, string SupportKey);
}

public sealed record TicketUpdateMessage(Guid Id, string Title, DateTimeOffset CreatedAt);

public sealed record TicketUpdateState(string DeviceId, DateTimeOffset? Cursor, Guid CursorId, int ActiveCount,
    DateTimeOffset UpdatedAtUtc, IReadOnlyList<TicketUpdateMessage> Messages)
{
    public static TicketUpdateState Empty(string deviceId) => new(deviceId, null, Guid.Empty, 0, DateTimeOffset.MinValue, []);
}
