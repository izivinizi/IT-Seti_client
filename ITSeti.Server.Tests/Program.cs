using System.Text.Json;
using ITSeti.Server;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using ITSeti.Maintenance.Core;

static JsonElement Report(string json) => JsonDocument.Parse(json).RootElement.Clone();
static void Check(bool value, string caseName)
{
    if (!value) throw new Exception(caseName);
    Console.WriteLine("PASS " + caseName);
}

var identity = DeviceIdentity.CreateFingerprint("  SN-1234 ", "11111111-1111-1111-1111-111111111111");
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
