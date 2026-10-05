using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace ITSeti.Server;

public sealed class OkdeskApiClient : IOkdeskClient
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = TimeSpan.FromSeconds(35) };

    private readonly IConfiguration configuration;
    private readonly HttpClient client;

    public OkdeskApiClient(IConfiguration configuration) : this(configuration, SharedClient) { }

    public OkdeskApiClient(IConfiguration configuration, HttpClient client)
    {
        this.configuration = configuration;
        this.client = client;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(configuration["OkdeskApiToken"]);
    public bool CanSendTickets => Configured;
    public bool CanSendComments => Configured && CommentAuthorId.HasValue;
    public bool CanReadComments => Configured;
    public bool CanChangeStatus => Configured;
    public long? CommentAuthorId => long.TryParse(configuration["OkdeskCommentAuthorId"], out var id) && id > 0 ? id : null;

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        long? fromId = null;
        for (var page = 0; page < 1000; page++)
        {
            var query = new Dictionary<string, string?>
            {
                ["page[size]"] = "100",
                ["page[direction]"] = "forward"
            };
            if (fromId.HasValue) query["page[from_id]"] = fromId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var document = await SendAsync(HttpMethod.Get, path, query, null, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Okdesk did not return a list.");
            var batch = document.RootElement.EnumerateArray().ToArray();
            if (batch.Length == 0) return items;
            items.AddRange(batch.Select(item => item.Clone()));
            if (items.Count > 100_000) throw new JsonException("Okdesk catalog exceeded the item limit.");
            var last = ReadId(batch[^1]);
            if (!last.HasValue || last == fromId) throw new JsonException("Okdesk pagination did not advance.");
            fromId = last;
            if (batch.Length < 100) return items;
        }
        throw new JsonException("Okdesk catalog exceeded the page limit.");
    }

    public async Task<long> CreateIssueAsync(OkdeskIssue issue, CancellationToken cancellationToken,
        IReadOnlyList<TicketAttachment>? attachments = null)
    {
        using HttpContent content = attachments is { Count: > 0 }
            ? BuildIssueMultipart(issue, attachments) : JsonContent.Create(new { issue });
        using var document = await SendAsync(HttpMethod.Post, "/api/v1/issues/", null, content, cancellationToken);
        return ReadId(document.RootElement) ?? throw new JsonException("Okdesk did not return an issue ID.");
    }

    public async Task<long> AddCommentAsync(long issueId, string text, IReadOnlyList<TicketAttachment> attachments,
        CancellationToken cancellationToken, bool isPublic = true)
    {
        var authorId = CommentAuthorId ?? throw new InvalidOperationException("Okdesk comment author is not configured.");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text)
            .Replace("\r\n", "<br />").Replace("\n", "<br />")), "comment[content]");
        content.Add(new StringContent(isPublic ? "true" : "false"), "comment[public]");
        content.Add(new StringContent(authorId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "comment[author_id]");
        content.Add(new StringContent("employee"), "comment[author_type]");
        AddFiles(content, "comment[attachments]", attachments);
        using var document = await SendAsync(HttpMethod.Post, $"/api/v1/issues/{issueId}/comments", null, content, cancellationToken);
        return ReadId(document.RootElement) ?? throw new JsonException("Okdesk did not return a comment ID.");
    }

    public async Task<IReadOnlyList<OkdeskComment>> GetCommentsAsync(long issueId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, $"/api/v1/issues/{issueId}/comments", null, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Okdesk did not return comments.");
        var comments = new List<OkdeskComment>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = ReadId(item);
            if (!id.HasValue || JsonRead.Property(item, "public") is not { ValueKind: JsonValueKind.True }) continue;
            var author = JsonRead.Property(item, "author") ?? default;
            comments.Add(new(id.Value, JsonRead.String(item, "content") ?? "",
                JsonRead.String(author, "type") ?? "employee",
                long.TryParse(JsonRead.String(author, "id"), out var authorId) ? authorId : null));
        }
        return comments;
    }

    public async Task SetIssueStatusAsync(long issueId, string target, CancellationToken cancellationToken)
    {
        if (target is not ("completed" or "cancelled")) throw new ArgumentException("Unknown status.");
        var code = target == "completed" ? "completed" : configuration["OkdeskCancelledStatusCode"] ?? "closed";
        using var content = JsonContent.Create(new { code,
            comment = target == "completed" ? "Работы выполнены через сервер ИТ-Сети." : "Заявка отменена через сервер ИТ-Сети.", comment_public = true });
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/issues/{issueId}/statuses", null, content, cancellationToken);
    }

    private static MultipartFormDataContent BuildIssueMultipart(OkdeskIssue issue, IReadOnlyList<TicketAttachment> attachments)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(issue.Title), "issue[title]");
        content.Add(new StringContent(issue.Description), "issue[description]");
        content.Add(new StringContent(issue.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "issue[company_id]");
        if (issue.MaintenanceEntityId.HasValue)
            content.Add(new StringContent(issue.MaintenanceEntityId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "issue[maintenance_entity_id]");
        content.Add(new StringContent(issue.Type), "issue[type]");
        foreach (var pair in issue.CustomParameters)
            content.Add(new StringContent(pair.Value), $"issue[custom_parameters][{pair.Key}]");
        AddFiles(content, "issue[attachments]", attachments);
        return content;
    }

    private static void AddFiles(MultipartFormDataContent content, string prefix, IReadOnlyList<TicketAttachment> attachments)
    {
        for (var i = 0; i < attachments.Count; i++)
        {
            var file = attachments[i];
            var body = new ByteArrayContent(file.Bytes);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(body, $"{prefix}[{i}][attachment]", file.FileName);
            content.Add(new StringContent("true"), $"{prefix}[{i}][is_public]");
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string?>? parameters,
        HttpContent? content, CancellationToken cancellationToken)
    {
        var token = configuration["OkdeskApiToken"]?.Trim();
        if (string.IsNullOrEmpty(token)) throw new OkdeskApiException(0);
        var baseUrl = configuration["OkdeskBaseUrl"] ?? "https://it-seti.okdesk.ru";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps ||
            !(baseUri.Host.Equals("okdesk.ru", StringComparison.OrdinalIgnoreCase) ||
              baseUri.Host.EndsWith(".okdesk.ru", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("OkdeskBaseUrl must be an Okdesk HTTPS host.");

        var query = new Dictionary<string, string?> { ["api_token"] = token };
        if (parameters is not null) foreach (var pair in parameters) query[pair.Key] = pair.Value;
        var uri = QueryHelpers.AddQueryString(new Uri(baseUri, path).ToString(), query);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new OkdeskApiException((int)response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static long? ReadId(JsonElement item)
    {
        var value = JsonRead.Property(item, "id");
        return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out var id) ? id : null;
    }
}

public sealed class OkdeskApiException(int statusCode) : Exception
{
    public int StatusCode { get; } = statusCode;
}
public sealed record OkdeskComment(long Id, string Content, string AuthorType, long? AuthorId);

public sealed record OkdeskIssue(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("company_id")] long CompanyId,
    [property: JsonPropertyName("maintenance_entity_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MaintenanceEntityId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("custom_parameters")] IReadOnlyDictionary<string, string> CustomParameters);

public static class TicketServices
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["517"] = "1С",
        ["518"] = "Сетевая инфраструктура",
        ["519"] = "Серверная инфраструктура",
        ["520"] = "АТС",
        ["521"] = "Видеонаблюдение",
        ["522"] = "Удаленный доступ",
        ["523"] = "Почта",
        ["524"] = "Права доступа",
        ["525"] = "Другое",
        ["615"] = "ЭЦП"
    };

    public static OkdeskIssue Build(string title, string description, string serviceCode, long companyId, long? siteId)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length is < 3 or > 200 ||
            string.IsNullOrWhiteSpace(description) || description.Trim().Length > 5000 ||
            !Names.ContainsKey(serviceCode) || companyId <= 0 || siteId is <= 0)
            throw new ArgumentException("Invalid ticket fields.");
        return new(title.Trim(), description.Trim(), companyId, siteId, "service",
            new Dictionary<string, string> { ["service_type"] = serviceCode });
    }
}
