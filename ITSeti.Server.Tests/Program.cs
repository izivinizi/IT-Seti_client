using System.Text.Json;
using ITSeti.Server;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using ITSeti.Maintenance.Core;

if (args.Contains("--okdesk-web-live") || args.Contains("--okdesk-ticket-live"))
{
    var liveConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OkdeskBaseUrl"] = Environment.GetEnvironmentVariable("OKDESK_BASE_URL"),
        ["OkdeskLogin"] = Environment.GetEnvironmentVariable("OKDESK_LOGIN"),
        ["OkdeskPassword"] = Environment.GetEnvironmentVariable("OKDESK_PASSWORD"),
        ["OkdeskWebTicketDeliveryEnabled"] = args.Contains("--okdesk-ticket-live") ? "true" : "false"
    }).Build();
    using var live = new OkdeskWebClient(liveConfig);
    if (!live.Configured) throw new InvalidOperationException("Live credentials are missing.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var companies = await live.GetAllAsync("/api/v1/companies/list", timeout.Token);
    if (args.Contains("--okdesk-ticket-live"))
    {
        var company = companies.Single(x => x.GetProperty("name").GetString()!.Contains("АйТи-Сети Групп", StringComparison.Ordinal));
        var attachment = new TicketAttachment(Guid.NewGuid(), "itseti-web-test.txt", "text/plain", Encoding.UTF8.GetBytes("ITSeti web integration test. No user data."));
        var id = long.TryParse(Environment.GetEnvironmentVariable("ITSETI_TEST_ISSUE_ID"), out var existing) ? existing :
            await live.CreateIssueAsync(TicketServices.Build("Тест интеграции ИТ-Сети — веб-отправка", "Техническая тестовая заявка. Проверка создания заявки и публичных комментариев без API. Работы не требуются.", "525", company.GetProperty("id").GetInt64(), null), timeout.Token, [attachment]);
        Console.WriteLine("LIVE TEST ISSUE: " + id);
        var publicId = await live.AddCommentAsync(id, "Тестовый публичный ответ ИТ-Сети.", [attachment], timeout.Token);
        Console.WriteLine("LIVE PUBLIC COMMENT: " + publicId);
        var privateId = await live.AddCommentAsync(id, "Тестовый скрытый комментарий. Клиент его видеть не должен.", [], timeout.Token, false);
        Console.WriteLine("LIVE PRIVATE COMMENT: " + privateId);
        var comments = await live.GetCommentsAsync(id, timeout.Token);
        if (!comments.Any(c => c.Id == publicId) || comments.Any(c => c.Id == privateId)) throw new Exception("Public/private comment boundary failed.");
        Console.WriteLine("PASS live creation with attachment, public reply with attachment and private comment exclusion");
        return;
    }
    var sites = await live.GetAllAsync("/api/v1/maintenance_entities/list", timeout.Token);
    var companyIds = companies.Select(x => x.GetProperty("id").GetInt64()).ToHashSet();
    Console.WriteLine($"Live catalog counts: companies={companies.Count}, objects={sites.Count}, unmatched owners={sites.Count(x => !companyIds.Contains(x.GetProperty("company_id").GetInt64()))}");
    if (companies.Count == 0 || sites.Any(x => x.GetProperty("company_id").GetInt64() <= 0))
        throw new Exception("Live catalog is incomplete.");
    Console.WriteLine($"PASS live web catalog: {companies.Count} companies, {sites.Count} objects; omitted owners remain unavailable for enrollment.");
    return;
}

static JsonElement Report(string json) => JsonDocument.Parse(json).RootElement.Clone();
static void Check(bool value, string caseName)
{
    if (!value) throw new Exception(caseName);
    Console.WriteLine("PASS " + caseName);
}

Check(OkdeskWebClient.ReadCreatedIssueId(Report("""{"redirect":{"redirect_path":"/issues/24447"}}""")) == 24447,
    "web creation reads the confirmed sequential ticket number");
var webComment = Report("""{"type":"AddCommentEvent","state":"published","isPublic":true,"commentId":42,"content":"public","author":{"type":"Employee","id":10}}""");
Check(OkdeskWebClient.ReadPublicComment(webComment).Id == 42, "published public web replies are parsed");
foreach (var rejected in new[] { webComment.GetRawText().Replace("true", "false"), webComment.GetRawText().Replace("published", "drafted"), webComment.GetRawText().Replace("\"isPublic\":true,", "") })
{
    try { OkdeskWebClient.ReadPublicComment(Report(rejected)); throw new Exception("private web reply accepted"); }
    catch (JsonException) { Check(true, "private, draft and unknown-visibility web replies are rejected"); }
}
var webFixture = new WebTicketHandler();
using (var web = new OkdeskWebClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["OkdeskLogin"]="fixture@example.test",["OkdeskPassword"]="fixture",["OkdeskWebTicketDeliveryEnabled"]="true"
}).Build(), new HttpClient(webFixture)))
{
    Check(await web.CreateIssueAsync(TicketServices.Build("Fixture", "<text>", "525", 123, 456), CancellationToken.None) == 731,
        "web transport creates a ticket after isolated authentication");
    Check(webFixture.Issue.GetProperty("company_id").GetInt64() == 123 && webFixture.Issue.GetProperty("maintenance_entity_id").GetInt64() == 456 &&
        webFixture.Issue.GetProperty("group_id").GetInt64() == 99 && webFixture.Issue.GetProperty("description").GetString() == "&lt;text&gt;",
        "web payload preserves assignment, service defaults and encodes user text");
    Check(await web.AddCommentAsync(731, "answer", [], CancellationToken.None) == 42 && webFixture.PublicReply,
        "nested web comment response confirms public delivery");
    Check((await web.GetCommentsAsync(731, CancellationToken.None)).Select(x => x.Id).SequenceEqual([42L]),
        "web history pagination excludes private replies");
}

var identity = DeviceIdentity.CreateFingerprint("  SN-1234 ", "11111111-1111-1111-1111-111111111111");
var webCompany = OkdeskWebClient.Normalize(Report("""{"id":"1014927","attributes":{"sequential_id":92,"name":"Fixture","active":true}}"""), true);
Check(webCompany.GetProperty("id").GetInt64() == 1014927, "web catalog preserves internal company IDs and existing device assignments");
var webSite = OkdeskWebClient.Normalize(Report("""{"id":"906876","attributes":{"name":"Office","isDisabled":true,"company":{"id":718775},"address":"Fixture address"}}"""), false);
Check(webSite.GetProperty("company_id").GetInt64() == 718775 && !webSite.GetProperty("active").GetBoolean(), "web catalog maps object owner and disabled state");
using (var web = new OkdeskWebClient(new ConfigurationBuilder().Build()))
    Check(!web.CanSendTickets && !web.CanReadComments && !web.CanChangeStatus, "unverified web ticket actions stay queued and private comments cannot leak");
var diagnosticRoot = Path.Combine(Path.GetTempPath(), "itseti-enrollment-" + Guid.NewGuid().ToString("N"));
try
{
    var diagnosticConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DataRoot"] = diagnosticRoot }).Build();
    var diagnosticLog = new EnrollmentDiagnostics(diagnosticConfig, Microsoft.Extensions.Logging.Abstractions.NullLogger<EnrollmentDiagnostics>.Instance);
    var diagnosticContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    diagnosticContext.TraceIdentifier = "fixture-request";
    diagnosticContext.Request.Path = "/api/v1/enroll";
    diagnosticContext.Request.Headers["X-Registration-Password"] = "secret-must-not-be-logged";
    diagnosticLog.Record(diagnosticContext, 400, "hardware_identity_unusable", new EnrollRequest(5, 6, "private-host", "private-serial", null, "0042"));
    var entry = diagnosticLog.Read().Single();
    Check(entry.Status == 400 && entry.Reason == "hardware_identity_unusable" && entry.HasSerial == true && entry.HasUuid == false,
        "enrollment rejection diagnostics persist with correlation and identity availability");
    var contents = File.ReadAllText(Path.Combine(diagnosticRoot, "enrollment-diagnostics.jsonl"));
    Check(!contents.Contains("secret-must-not-be-logged") && !contents.Contains("private-host") && !contents.Contains("private-serial"),
        "enrollment diagnostics exclude credentials and raw hardware identifiers");
    File.WriteAllText(Path.Combine(diagnosticRoot, "enrollment-diagnostics.jsonl"), new string(' ', 1024 * 1024 + 1));
    diagnosticLog.Record(diagnosticContext, 200, "completed");
    Check(diagnosticLog.Read().Count == 1 && File.Exists(Path.Combine(diagnosticRoot, "enrollment-diagnostics.jsonl.previous")),
        "enrollment diagnostics rotate within a bounded disk budget");
}
finally { if (Directory.Exists(diagnosticRoot)) Directory.Delete(diagnosticRoot, true); }
Check(JsonRead.Date(Report("""{"StartedAt":"2026-10-05T00:10:36+05:00"}"""), "StartedAt") ==
    new DateTimeOffset(2026, 10, 4, 19, 10, 36, TimeSpan.Zero), "report dates preserve instant in UTC");
Check(JsonRead.Date(Report("""{"StartedAt":"2026-10-05T00:10:36+05:00"}"""), "StartedAt")?.Offset == TimeSpan.Zero,
    "report dates have PostgreSQL-compatible zero offset");
Check(identity == DeviceIdentity.CreateFingerprint("SN1234", null), "hardware serial creates stable install identity");
Check(DeviceIdentity.CreateFingerprint("Default String", "{12345678-1234-1234-1234-123456789ABC}") ==
    DeviceIdentity.CreateFingerprint(null, "12345678123412341234123456789ABC"), "generic serial falls back to stable hardware UUID");
Check(DeviceIdentity.CreateFingerprint("To Be Filled By O.E.M.", "00000000-0000-0000-0000-000000000000") is null,
    "placeholder hardware identifiers do not merge computers");

var first = Report("""
    {"Notes":["CPU: температура 78 °C достигла порога 75 °C."],"Full":{"Events":[
      {"Log":"System","Provider":"Disk","Id":7,"Level":2,"RecordId":10,"Message":"Bad block"}]}}
    """);
Check(Monitoring.NewIssues(first, null).Count == 0, "first report establishes a baseline");

var repeated = Report("""
    {"Notes":["CPU: температура 80 °C достигла порога 75 °C."],"Full":{"Events":[
      {"Log":"System","Provider":"Disk","Id":7,"Level":2,"RecordId":11,"Message":"Bad block again"}]}}
    """);
Check(Monitoring.NewIssues(repeated, first).Count == 0, "changing readings and record IDs do not repeat alerts");

var oldReadings = Report("""{"Notes":["Наработка после последнего запуска: 61 ч.","C:\\ свободно 8,1 ГБ"]}""");
var newReadings = Report("""{"Notes":["Наработка после последнего запуска: 63 ч.","C:\\ свободно 4,2 ГБ"]}""");
Check(Monitoring.NewIssues(newReadings, oldReadings).Count == 1, "warning escalating to critical generates an alert");

var newError = Report("""
    {"Notes":["CPU: температура 80 °C достигла порога 75 °C.","ОЗУ: менее 500 МБ доступно и активная подкачка"],
     "Full":{"Events":[
       {"Log":"System","Provider":"Disk","Id":7,"Level":2,"RecordId":12,"Message":"Still present"},
       {"Log":"System","Provider":"Ntfs","Id":55,"Level":1,"RecordId":13,"Message":"Corrupt volume"},
       {"Log":"System","Provider":"Ntfs","Id":55,"Level":1,"RecordId":14,"Message":"Corrupt volume"}]}}
    """);
var newIssues = Monitoring.NewIssues(newError, first);
Check(newIssues.Count == 1 && newIssues.Count(i => i.Category == "event") == 1,
    "only new critical issues enter monitoring and duplicates are suppressed");

var quick = Report("""{"Notes":[]}""");
Check(Monitoring.NewIssues(newError, quick).All(i => i.Category != "event"), "first full event sample is a baseline");
Check(Monitoring.NewIssues(newError, quick, first).Count(i => i.Category == "event") == 1,
    "a quick check between full checks does not erase the event baseline");

var powerZero = Report("""
    {"Full":{"Events":[{"Log":"System","Provider":"Microsoft-Windows-Kernel-Power","Id":41,
      "Level":1,"Message":"BugcheckCode: 0"}]}}
    """);
Check(Monitoring.NewIssues(powerZero, first).Count == 0, "one zero-code power loss is ignored");

var win7Old = Report("""{"Findings":[],"Events":[{"Log":"Application","Source":"App","Id":100,"Level":1,"Message":"Error"}]}""");
var win7New = Report("""{"Findings":[],"Events":[{"Log":"Application","Source":"App","Id":101,"Level":1,"Message":"Error"}]}""");
Check(Monitoring.NewIssues(win7New, win7Old).Single().Title == "App #101", "Windows 7 event format is supported");
var declared = Report("""{"DiagnosticIssues":[{"Title":"Критическая проблема","Detail":"Диск неисправен","Severity":"Critical"},{"Title":"Предупреждение","Detail":"Мало памяти","Severity":"Warning"}],"Notes":["Пароль уже бессрочный"]}""");
Check(Monitoring.CurrentIssues(declared).Count == 2 && Monitoring.NewIssues(declared, quick).Single().Severity == 1,
    "client warnings remain in results but only client critical issues become alerts");
Check(Monitoring.CurrentIssues(Report("""{"Notes":["Пароль уже бессрочный","Без доступа к исполняемому файлу процессов: 13","Автоматическая установка обновлений отключена."]}""")).Count == 0,
    "maintenance messages are not diagnostic issues");
var bsod = Report("""{"Full":{"Events":[{"Provider":"Microsoft-Windows-Kernel-Power","Id":41,"Level":1,"Message":"BugcheckCode: 209"}]}}""");
Check(Monitoring.CurrentIssues(bsod).Single().Title.Contains("DRIVER_IRQL_NOT_LESS_OR_EQUAL"), "bugcheck code is decoded on the server");
var snapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "fixture", 12, 8UL * 1073741824,
    4UL * 1073741824, [], ["Пароль уже бессрочный"], CpuTemperatureC: 86);
var serialized = JsonSerializer.SerializeToElement(snapshot);
Check(Monitoring.CurrentIssues(serialized).Single().Title == DiagnosticRules.GetUserIssues(snapshot).Single().Title,
    "server classification matches client diagnostic rules on legacy reports");
Check(Monitoring.NewIssues(declared, quick, includeWarnings: true).Count == 2,
    "warnings remain available through an explicit monitoring filter");
var publicOnly = new CommentApiHandler();
var commentClient = new OkdeskApiClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["OkdeskApiToken"]="test-token",["OkdeskBaseUrl"]="https://it-seti.okdesk.ru" }).Build(), new HttpClient(publicOnly));
Check((await commentClient.GetCommentsAsync(731, CancellationToken.None)).Select(c => c.Id).SequenceEqual([1L]),
    "private and unspecified-visibility Okdesk comments never enter client conversation");

var ticket = TicketServices.Build("Тестовая заявка", "Описание", "525", 12, null);
var policyRoot = Path.Combine(Path.GetTempPath(), "itseti-policy-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(policyRoot);
try
{
    var policy = new ProcessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DataRoot"]=policyRoot }).Build());
    await policy.WriteAsync(new(["chrome.exe","CHROME.exe"],["Microsoft*"]));
    Check(policy.Read().AllowedNames.Length == 1 && policy.IsAllowed(Report("""{"Name":"chrome"}""")), "process policy normalizes names and duplicates");
    Check(policy.IsAllowed(Report("""{"Name":"fixture","Signature":"Valid","Publisher":"Microsoft Corporation"}""")) &&
        !policy.IsAllowed(Report("""{"Name":"fixture","Signature":"NotSigned","Publisher":"Microsoft Corporation"}""")),
        "publisher exceptions require a valid signature");
    var restored = new ProcessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DataRoot"]=policyRoot }).Build());
    Check(restored.Read().AllowedNames.Single() == "chrome.exe", "process policy survives restart");
    File.WriteAllText(Path.Combine(policyRoot, "process-policy.json"), "{broken");
    var recovered = new ProcessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DataRoot"]=policyRoot }).Build());
    Check(recovered.Read().AllowedNames.Length > 0 && File.ReadAllText(Path.Combine(policyRoot,"process-policy.json")) == "{broken",
        "damaged saved policy does not stop the server or overwrite diagnostic evidence");
}
finally { Directory.Delete(policyRoot, true); }
Check(ticket.CompanyId == 12 && ticket.MaintenanceEntityId is null &&
    ticket.CustomParameters["service_type"] == "525", "ticket uses server assignment and service directory code");
try
{
    TicketServices.Build("Тестовая заявка", "Описание", "999", 12, null);
    throw new Exception("unknown ticket service was accepted");
}
catch (ArgumentException) { Check(true, "unknown ticket service is rejected"); }

var handler = new TicketApiHandler();
Check(TicketAttachments.Decode([new("note.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes("test")))]).Single().Bytes.Length == 4,
    "valid attachment is decoded");
foreach (var invalid in new TicketAttachmentInput[] { null!, new(null!, "AAAA"), new("a.txt", null!), new("../a.txt", "AAAA"), new("a.exe", "AAAA"), new("a.txt", "bad!") })
{
    try { TicketAttachments.Decode([invalid]); throw new Exception("invalid attachment accepted"); }
    catch (ArgumentException) { Check(true, "invalid or null attachment rejected without server exception"); }
}
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["OkdeskApiToken"] = "test-token", ["OkdeskBaseUrl"] = "https://it-seti.okdesk.ru"
}).Build();
var client = new OkdeskApiClient(config, new HttpClient(handler));
Check(await client.CreateIssueAsync(ticket, CancellationToken.None) == 731, "Okdesk issue ID is read");
Check(handler.Path == "/api/v1/issues/" && handler.Query.Contains("api_token=test-token") &&
    handler.Body.Contains("\"company_id\":12") && handler.Body.Contains("\"service_type\":\"525\"") &&
    !handler.Body.Contains("maintenance_entity_id"), "Okdesk API request has the expected fields");

sealed class TicketApiHandler : HttpMessageHandler
{
    public string Path { get; private set; } = "";
    public string Query { get; private set; } = "";
    public string Body { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Path = request.RequestUri!.AbsolutePath;
        Query = request.RequestUri.Query;
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":731}", Encoding.UTF8, "application/json")
        };
    }
}

sealed class CommentApiHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(
            """[{"id":1,"public":true,"content":"public"},{"id":2,"public":false,"content":"private"},{"id":3,"content":"unknown"}]""", Encoding.UTF8,"application/json")});
}

sealed class WebTicketHandler : HttpMessageHandler
{
    public JsonElement Issue { get; private set; }
    public bool PublicReply { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        string json;
        switch (path)
        {
            case "/web_api/layouts/auth": json = """{"csrfToken":"fixture"}"""; break;
            case "/web_api/sessions": json = "{}"; break;
            case "/web_api/layouts/app": json = """{"csrfToken":"fixture","currentUser":{"id":10,"email":"fixture@example.test"}}"""; break;
            case "/issues/create_forms/new":
                json = JsonSerializer.Serialize(new { html_content = new { append = new Dictionary<string,string> { ["form"] = "<div collection_url='/collections/assignees?option_id=fixture' selected_text='Support'></div>" } }, options = new {type_id=1,priority_id=2} }); break;
            case "/collections/assignees?option_id=fixture":
                json = JsonSerializer.Serialize(new { html_content = new { html = new Dictionary<string,string> { ["groups"] = "<div name='group_id' value='99'><span data-text-value='Техническая поддержка'></span></div>" } } }); break;
            case "/issues":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))) Issue = body.RootElement.GetProperty("issue").Clone();
                json = """{"redirect":{"redirect_path":"/issues/731"}}"""; break;
            case "/issues/731/history_events/comments":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))) PublicReply = body.RootElement.GetProperty("is_public").GetBoolean();
                json = """{"type":"published","record":{"record":{"commentId":42}}}"""; break;
            case "/issues/731/history_events":
                json = """{"isLastPage":false,"historyEvents":[{"record":{"id":101,"commentId":43,"type":"AddCommentEvent","state":"published","isPublic":false}}]}"""; break;
            case "/issues/731/history_events?event_id=101":
                json = """{"isLastPage":true,"historyEvents":[{"record":{"id":100,"commentId":42,"type":"AddCommentEvent","state":"published","isPublic":true,"content":"answer","author":{"type":"Employee","id":10}}}]}"""; break;
            default: throw new Exception("Unexpected web endpoint: " + path);
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
