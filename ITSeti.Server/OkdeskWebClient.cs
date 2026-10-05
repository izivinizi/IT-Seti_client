using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace ITSeti.Server;

// The web transport uses an isolated workspace, never the operator's saved filters.
public sealed class OkdeskWebClient : IOkdeskClient, IDisposable
{
    private readonly IConfiguration configuration;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? csrf;
    private DateTimeOffset authenticatedUntil;

    public OkdeskWebClient(IConfiguration configuration) : this(configuration, new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = TimeSpan.FromSeconds(35) }) { }

    public OkdeskWebClient(IConfiguration configuration, HttpClient client)
    {
        this.configuration = configuration;
        this.client = client;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(configuration["OkdeskLogin"]) &&
        !string.IsNullOrWhiteSpace(configuration["OkdeskPassword"]);
    private bool TicketDeliveryEnabled => Configured && configuration.GetValue<bool>("OkdeskWebTicketDeliveryEnabled");
    public bool CanSendTickets => TicketDeliveryEnabled;
    public bool CanSendComments => TicketDeliveryEnabled;
    public bool CanReadComments => TicketDeliveryEnabled;
    public bool CanChangeStatus => false;
    public long? CommentAuthorId { get; private set; }

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken cancellationToken)
    {
        var (entity, collection) = path switch
        {
            "/api/v1/companies/list" => ("company", "/companies"),
            "/api/v1/maintenance_entities/list" => ("maintenance_entity", "/maintenance_entities"),
            _ => throw new InvalidOperationException("Unsupported web catalog.")
        };
        await gate.WaitAsync(cancellationToken);
        try
        {
            await AuthenticateAsync(cancellationToken);
            var workspace = await GetWorkspaceAsync(entity, cancellationToken);
            var items = new List<JsonElement>();
            var ids = new HashSet<long>();
            int? expectedPages = null;
            for (var page = 1; page <= 1000; page++)
            {
                using var data = await SendAsync(HttpMethod.Get, $"{collection}?entity_id={workspace}&page={page}", null, cancellationToken);
                var pages = data.RootElement.GetProperty("total_pages").GetInt32();
                if (pages < 0 || pages > 1000 || expectedPages.HasValue && pages != expectedPages)
                    throw new JsonException("Catalog changed during pagination.");
                expectedPages = pages;
                foreach (var row in data.RootElement.GetProperty("records").GetProperty("data").EnumerateArray())
                {
                    var item = Normalize(row, entity == "company");
                    var id = item.GetProperty("id").GetInt64();
                    if (!ids.Add(id)) throw new JsonException("Catalog pagination repeated a record.");
                    items.Add(item);
                    if (items.Count > 100_000) throw new JsonException("Catalog exceeds the limit.");
                }
                if (page >= pages) return items;
            }
            throw new JsonException("Catalog exceeds the page limit.");
        }
        finally { gate.Release(); }
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        if (authenticatedUntil > DateTimeOffset.UtcNow) return;
        using var auth = await SendAsync(HttpMethod.Get, "/web_api/layouts/auth", null, ct);
        csrf = auth.RootElement.GetProperty("csrfToken").GetString() ?? throw new JsonException("Missing CSRF token.");
        using var credentials = JsonContent.Create(new { user = new
        {
            login = configuration["OkdeskLogin"], password = configuration["OkdeskPassword"], remember_me = false
        } });
        using var login = await SendAsync(HttpMethod.Post, "/web_api/sessions", credentials, ct);
        using var app = await SendAsync(HttpMethod.Get, "/web_api/layouts/app", null, ct);
        var user = app.RootElement.GetProperty("currentUser");
        if (user.ValueKind != JsonValueKind.Object ||
            !string.Equals(JsonRead.String(user, "email"), configuration["OkdeskLogin"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Okdesk did not authenticate the expected account.");
        csrf = JsonRead.String(app.RootElement, "csrfToken") ?? csrf;
        CommentAuthorId = ReadId(user.GetProperty("id"));
        authenticatedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private async Task<long> GetWorkspaceAsync(string entity, CancellationToken ct)
    {
        var name = $"ITSeti sync ({entity})";
        using var workspaces = await SendAsync(HttpMethod.Get, $"/entity_workspaces?entity_type={entity}", null, ct);
        long? id = null;
        foreach (var row in workspaces.RootElement.GetProperty("collection").EnumerateArray())
            if (JsonRead.String(row.GetProperty("attributes"), "name") == name) id = ReadId(row.GetProperty("id"));
        if (!id.HasValue)
        {
            var source = ReadId(workspaces.RootElement.GetProperty("collection")[0].GetProperty("id"));
            using var body = JsonContent.Create(new { entity_type = entity, source_entity_id = source });
            using var created = await SendAsync(HttpMethod.Post, "/entity_workspaces", body, ct);
            id = ReadId(created.RootElement.GetProperty("item").GetProperty("id"));
            using var rename = JsonContent.Create(new { name, entity_type = entity });
            using var renamed = await SendAsync(HttpMethod.Patch, $"/entity_workspaces/{id}", rename, ct);
        }
        // Reset only our workspace, not the workspace currently selected in the browser.
        using var reset = await SendAsync(HttpMethod.Delete, $"/entity_workspaces/global_filters/resets?entity_id={id}", null, ct);
        return id.Value;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var origin = new Uri(configuration["OkdeskBaseUrl"] ?? "https://it-seti.okdesk.ru");
        if (origin.Scheme != "https" || !origin.Host.EndsWith(".okdesk.ru", StringComparison.OrdinalIgnoreCase) ||
            !origin.IsDefaultPort || !string.IsNullOrEmpty(origin.UserInfo))
            throw new InvalidOperationException("Invalid Okdesk origin.");
        using var request = new HttpRequestMessage(method, new Uri(origin.GetLeftPart(UriPartial.Authority) + path));
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("ResponseFormat", "serialized_object");
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        request.Content = content;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            authenticatedUntil = default;
            throw new OkdeskApiException((int)response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new JsonException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + read > 16 * 1024 * 1024) throw new JsonException("Response too large.");
            buffer.Write(block, 0, read);
        }
        var document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            document.Dispose();
            throw new JsonException("Okdesk rejected the web request.");
        }
        return document;
    }

    public static JsonElement Normalize(JsonElement row, bool company)
    {
        var attrs = row.GetProperty("attributes");
        var id = ReadId(row.GetProperty("id"));
        var name = JsonRead.String(attrs, "name") ?? throw new JsonException("Missing catalog name.");
        var active = JsonRead.Property(attrs, "active")?.ValueKind != JsonValueKind.False &&
            JsonRead.Property(attrs, "isDisabled")?.ValueKind != JsonValueKind.True;
        if (company) return JsonSerializer.SerializeToElement(new { id, name, active });
        var companyId = ReadId(attrs.GetProperty("company").GetProperty("id"));
        return JsonSerializer.SerializeToElement(new { id, name, active, company_id = companyId,
            company_name = JsonRead.String(attrs.GetProperty("company"), "name"), address = JsonRead.String(attrs, "address") });
    }

    private static long ReadId(JsonElement value)
    {
        if (long.TryParse(value.ToString(), out var id) && id > 0) return id;
        throw new JsonException("Invalid Okdesk ID.");
    }

    public async Task<long> CreateIssueAsync(OkdeskIssue issue, CancellationToken ct, IReadOnlyList<TicketAttachment>? attachments = null)
    {
        if (!CanSendTickets) throw new InvalidOperationException("Web ticket delivery is disabled.");
        await gate.WaitAsync(ct);
        try
        {
            await AuthenticateAsync(ct);
            using var form = await SendAsync(HttpMethod.Get, "/issues/create_forms/new", null, ct);
            var defaults = ReadIssueDefaults(form.RootElement);
            using var assignees = await SendAsync(HttpMethod.Get, defaults.AssigneesPath, null, ct);
            var group = ReadDefaultGroup(assignees.RootElement, configuration["OkdeskWebDefaultGroup"] ?? "Техническая поддержка");
            var uploaded = new List<object>();
            foreach (var file in attachments ?? [])
            {
                using var upload = FileContent(file, "temp_attachment[attachment]");
                using var response = await SendAsync(HttpMethod.Post, "/temporary_attachments", upload, ct);
                var uuid = JsonRead.String(response.RootElement, "uuid") ?? throw new JsonException("Missing uploaded attachment UUID.");
                uploaded.Add(new { uuid, description = "", is_public = true });
            }
            using var body = JsonContent.Create(new { issue = new
            {
                title = issue.Title, description = EncodeText(issue.Description), company_id = issue.CompanyId,
                maintenance_entity_id = issue.MaintenanceEntityId, work_type_id = defaults.TypeId,
                priority_id = defaults.PriorityId, group_id = group,
                ftselect_directory_parameters = issue.CustomParameters, temp_attachments = uploaded
            } });
            using var created = await SendAsync(HttpMethod.Post, "/issues", body, ct);
            return ReadCreatedIssueId(created.RootElement);
        }
        finally { gate.Release(); }
    }

    public async Task<long> AddCommentAsync(long id, string text, IReadOnlyList<TicketAttachment> files, CancellationToken ct, bool isPublic = true)
    {
        if (!CanSendTickets || id <= 0) throw new InvalidOperationException("Web comment delivery is disabled.");
        await gate.WaitAsync(ct);
        try
        {
            await AuthenticateAsync(ct);
            var uploaded = new List<long>();
            foreach (var file in files)
            {
                using var upload = FileContent(file, "temp_attachment[attachment][attachment]");
                using var response = await SendAsync(HttpMethod.Post, "/temp_attachments", upload, ct);
                uploaded.Add(ReadId(response.RootElement.GetProperty("id")));
            }
            using var body = JsonContent.Create(new { content = EncodeText(text), is_public = isPublic,
                send_to_partner = false, temp_attachments_ids = uploaded });
            using var added = await SendAsync(HttpMethod.Post, $"/issues/{id}/history_events/comments", body, ct);
            if (JsonRead.String(added.RootElement, "type") == "drafted") throw new JsonException("Comment was not published.");
            var record = added.RootElement.GetProperty("record");
            if (record.TryGetProperty("record", out var nested)) record = nested;
            return ReadId(record.GetProperty("commentId"));
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<OkdeskComment>> GetCommentsAsync(long id, CancellationToken ct)
    {
        if (!CanReadComments || id <= 0) throw new InvalidOperationException("Web comment sync is disabled.");
        await gate.WaitAsync(ct);
        try
        {
            await AuthenticateAsync(ct);
            var comments = new List<OkdeskComment>();
            var eventIds = new HashSet<long>();
            long? before = null;
            for (var page = 0; page < 100; page++)
            {
                using var history = await SendAsync(HttpMethod.Get, $"/issues/{id}/history_events" +
                    (before.HasValue ? $"?event_id={before}" : ""), null, ct);
                var events = history.RootElement.GetProperty("historyEvents").EnumerateArray().ToArray();
                foreach (var entry in events)
                {
                    var record = entry.GetProperty("record");
                    var eventId = ReadId(record.GetProperty("id"));
                    if (!eventIds.Add(eventId)) throw new JsonException("History pagination repeated an event.");
                    before = eventId;
                    if (JsonRead.String(record, "type") != "AddCommentEvent" ||
                        JsonRead.Property(record, "isPublic")?.ValueKind != JsonValueKind.True ||
                        JsonRead.String(record, "state") != "published") continue;
                    comments.Add(ReadPublicComment(record));
                }
                if (history.RootElement.GetProperty("isLastPage").GetBoolean()) return comments;
                if (events.Length == 0) throw new JsonException("History pagination did not advance.");
            }
            throw new JsonException("History exceeds the limit.");
        }
        finally { gate.Release(); }
    }

    public static OkdeskComment ReadPublicComment(JsonElement record)
    {
        if (JsonRead.Property(record, "isPublic")?.ValueKind != JsonValueKind.True ||
            JsonRead.String(record, "state") != "published" || JsonRead.String(record, "type") != "AddCommentEvent")
            throw new JsonException("Private or unpublished comment.");
        var author = record.GetProperty("author");
        return new(ReadId(record.GetProperty("commentId")), JsonRead.String(record, "content") ?? "",
            JsonRead.String(author, "type") ?? "", ReadId(author.GetProperty("id")));
    }

    public static long ReadCreatedIssueId(JsonElement response)
    {
        // Web links use the sequential issue number, not the internal database ID.
        var value = JsonRead.Property(response, "redirect");
        var redirect = value is { ValueKind: JsonValueKind.Object } ? JsonRead.String(value.Value, "redirect_path") : JsonRead.String(response, "redirect");
        if (redirect is not null && Uri.TryCreate(new Uri("https://it-seti.okdesk.ru"), redirect, out var uri) &&
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["issues", var number] &&
            long.TryParse(number, out var id) && id > 0) return id;
        throw new JsonException("Missing confirmed issue link. Response fields: " +
            string.Join(",", response.EnumerateObject().Select(p => p.Name)));
    }

    private static MultipartFormDataContent FileContent(TicketAttachment file, string field)
    {
        if (file.Bytes.Length is < 1 or > 5 * 1024 * 1024) throw new ArgumentException("Invalid attachment size.");
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(file.Bytes);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(bytes, field, Path.GetFileName(file.FileName));
        return content;
    }

    private static string EncodeText(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text)
        .Replace("\r\n", "<br />").Replace("\n", "<br />");

    public sealed record IssueDefaults(long TypeId, long PriorityId, string GroupName, string AssigneesPath);
    public static IssueDefaults ReadIssueDefaults(JsonElement form)
    {
        var html = form.GetProperty("html_content").GetProperty("append").EnumerateObject().Single().Value.GetString()!;
        using var document = new HtmlParser().ParseDocument(html);
        var options = form.GetProperty("options");
        var group = document.QuerySelector("[collection_url^='/collections/assignees?']") ?? throw new JsonException("Missing assignee selector.");
        var path = group.GetAttribute("collection_url")!;
        if (!path.StartsWith("/collections/assignees?", StringComparison.Ordinal) || path.Contains("\\"))
            throw new JsonException("Unexpected assignee collection.");
        return new(ReadId(options.GetProperty("type_id")), ReadId(options.GetProperty("priority_id")),
            group.GetAttribute("selected_text") ?? group.QuerySelector(".selected")?.TextContent.Trim()
                ?? throw new JsonException("Missing default assignee."), path);
    }

    private static long ReadDefaultGroup(JsonElement response, string name)
    {
        var html = string.Join("", response.GetProperty("html_content").GetProperty("html").EnumerateObject().Select(x => x.Value.GetString()));
        using var document = new HtmlParser().ParseDocument(html);
        var group = document.QuerySelectorAll("[name='group_id'][value]").SingleOrDefault(x =>
            x.QuerySelector("[data-text-value]")?.GetAttribute("data-text-value") == name);
        if (group is null || !long.TryParse(group.GetAttribute("value"), out var id) || id <= 0)
            throw new JsonException("Default support group is unavailable.");
        return id;
    }

    public Task SetIssueStatusAsync(long id, string target, CancellationToken ct) =>
        throw new InvalidOperationException("Web status changes are not enabled.");
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
