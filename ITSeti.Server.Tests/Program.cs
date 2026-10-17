using System.Text.Json;
using ITSeti.Server;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ITSeti.Maintenance.Core;

TicketMergeChecks.Run();

if (args.Contains("--feedback-database"))
{
    await FeedbackDatabaseChecks.RunAsync();
    return;
}

if (args.Contains("--monitoring-performance"))
{
    MonitoringPerformanceContract.Run();
    return;
}

if (args.Length == 2 && args[0] == "--win7-report-contract")
{
    using var document = JsonDocument.Parse(File.ReadAllText(args[1]));
    var issues = Monitoring.CurrentIssues(document.RootElement);
    if (issues.Count != 1 || issues[0].Severity != 1 || !issues[0].Title.Contains("температура"))
        throw new Exception("Win7/net48 report disagrees with server/Core");
    Console.WriteLine("PASS actual net48 serialized report -> server: identical critical CPU result");
    return;
}

if (args.Contains("--okdesk-web-live") || args.Contains("--okdesk-ticket-live") || args.Contains("--okdesk-sync-live") || args.Contains("--okdesk-equipment-live"))
{
    var liveConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OkdeskBaseUrl"] = Environment.GetEnvironmentVariable("OKDESK_BASE_URL"),
        ["OkdeskLogin"] = Environment.GetEnvironmentVariable("OKDESK_LOGIN"),
        ["OkdeskPassword"] = Environment.GetEnvironmentVariable("OKDESK_PASSWORD"),
        ["OkdeskWebTicketDeliveryEnabled"] = args.Contains("--okdesk-ticket-live") || args.Contains("--okdesk-sync-live") ? "true" : "false"
    }).Build();
    using var live = new OkdeskWebClient(liveConfig);
    if (!live.Configured) throw new InvalidOperationException("Live credentials are missing.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    if (args.Contains("--okdesk-sync-live"))
    {
        var id = long.Parse(Environment.GetEnvironmentVariable("ITSETI_TEST_ISSUE_ID") ?? throw new Exception("Read-only test issue is required."));
        var comments = await live.GetCommentsAsync(id, timeout.Token);
        typeof(OkdeskWebClient).GetField("authenticatedUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(live, DateTimeOffset.MinValue);
        var refreshedComments = await live.GetCommentsAsync(id, timeout.Token);
        if (comments.Count != refreshedComments.Count || comments.Any(c => c.Content.Contains("Тестовый скрытый комментарий"))) throw new Exception("Refresh or privacy boundary failed.");
        var status = await live.GetIssueStatusAsync(id, timeout.Token);
        Console.WriteLine($"PASS read-only live sync: {comments.Count} public replies; status={status?.Name}; session refresh succeeded");
        var file = comments.SelectMany(c => c.Attachments ?? []).First();
        var bytes = await live.DownloadAttachmentAsync(id, file, timeout.Token);
        if (bytes.Length != file.Size) throw new Exception("Download size mismatch");
        Console.WriteLine($"PASS live public attachment downloaded: {bytes.Length} bytes");
        return;
    }
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
    if (args.Contains("--okdesk-equipment-live"))
    {
        try
        {
            var equipment = await live.GetAllAsync("/api/v1/equipments/list", timeout.Token);
            Console.WriteLine($"PASS live web equipment: {equipment.Count} computers; without inventory={equipment.Count(x => x.GetProperty("inventory_number").ValueKind == JsonValueKind.Null)}, without company={equipment.Count(x => x.GetProperty("company_id").ValueKind == JsonValueKind.Null)}, without object={equipment.Count(x => x.GetProperty("maintenance_entity_id").ValueKind == JsonValueKind.Null)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or OkdeskApiException)
        {
            Console.WriteLine("LIVE web equipment failed: " + ex.GetType().Name);
            Environment.ExitCode = 2;
        }
        return;
    }
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

var sourceRoot = Environment.GetEnvironmentVariable("ITSETI_TEST_SOURCE_ROOT")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var applicationErrorSchema = File.ReadAllText(Path.Combine(sourceRoot, "ITSeti.Server", "Database.cs"));
var applicationErrorApi = File.ReadAllText(Path.Combine(sourceRoot, "ITSeti.Server", "Program.cs"));
var applicationErrorPage = File.ReadAllText(Path.Combine(sourceRoot, "ITSeti.Server", "wwwroot", "index.html"));
var applicationErrorUi = File.ReadAllText(Path.Combine(sourceRoot, "ITSeti.Server", "wwwroot", "app.js"));
Check(applicationErrorSchema.Contains("CREATE TABLE IF NOT EXISTS application_errors") &&
    applicationErrorSchema.Contains("PRIMARY KEY(device_id,error_id)"),
    "application error journal has an idempotent per-device schema");
Check(applicationErrorApi.Contains("app.MapPost(\"/api/v1/application-errors\"") &&
    applicationErrorApi.Contains("admin.MapGet(\"/application-errors\"") &&
    applicationErrorApi.Contains("DeviceDisplayFilter.SqlPredicate"),
    "application error upload is device-authenticated and admin feed hides excluded computers");
Check(applicationErrorPage.Contains("data-view=\"application-errors\"") &&
    applicationErrorPage.Contains("id=\"app-errors-list\"") &&
    applicationErrorPage.Contains("id=\"app-errors-company\""),
    "admin UI exposes a filterable client application-error journal");
Check(applicationErrorSchema.Contains("hardware_profile jsonb") && applicationErrorSchema.Contains("sensor_history jsonb") &&
    applicationErrorSchema.Contains("monitor_issue_ignores") && applicationErrorApi.Contains("BuildHardwareProfile(report.RootElement)") &&
    applicationErrorApi.Contains("TryReadSensorHistory(item, out var sensorHistory)"),
    "client reports retain hardware configuration and bounded sensor history");
Check(applicationErrorApi.Contains("admin.MapPost(\"/monitoring/{id:long}/resolve\"") &&
    applicationErrorApi.Contains("admin.MapPost(\"/monitoring/{id:long}/ignore\"") &&
    applicationErrorApi.Contains("state='resolved'") && applicationErrorApi.Contains("state='ignored'") &&
    applicationErrorUi.Contains("Игнорировать эту причину"),
    "monitoring issues can be resolved or explicitly ignored for one computer");
Check(applicationErrorUi.Contains("application-error-card clickable") && applicationErrorUi.Contains("monitor-card clickable") &&
    applicationErrorUi.Contains("showTelemetryChart") && applicationErrorUi.Contains("Math.round(Number(cpu)) + '%'"),
    "server groups issues by PC, charts recent sensor history and rounds CPU load");
var ticketRouteStart = applicationErrorApi.IndexOf("admin.MapGet(\"/tickets\"", StringComparison.Ordinal);
var ticketRouteEnd = applicationErrorApi.IndexOf("admin.MapGet(\"/overview\"", ticketRouteStart, StringComparison.Ordinal);
var ticketRoute = ticketRouteStart >= 0 && ticketRouteEnd > ticketRouteStart
    ? applicationErrorApi[ticketRouteStart..ticketRouteEnd] : "";
Check(ticketRoute.Contains("new NpgsqlCommand($\"\"\"") && ticketRoute.Contains("{DeviceDisplayFilter.SqlPredicate}"),
    "ticket list SQL interpolates its display filter instead of sending braces to PostgreSQL");

Check(PackageVersionDetector.TryParse("FileVersion 2.3.3.0", out var detectedVersion) && detectedVersion == new Version(2, 3, 3, 0),
    "application package versions accept EXE FileVersion metadata");
Check(PackageVersionDetector.TryParse("ProductVersion 7.7.3.0", out var productVersion) && productVersion == new Version(7, 7, 3, 0),
    "software package versions accept ProductVersion metadata before FileVersion");
Check(PackageVersionDetector.TryParse("ITSeti-Maintenance-Setup-2.3.3.exe", out var fileNameVersion) && fileNameVersion == new Version(2, 3, 3),
    "application package versions fall back to a semantic version in the filename");
Check(PackageVersionDetector.DetectName("OCS Inventory NG Agent", "Setup", "agent-2.11.0.exe") == "OCS Inventory NG Agent" &&
    PackageVersionDetector.DetectName("", "RMS Host Installer", "rms.exe") == "RMS Host Installer" &&
    PackageVersionDetector.DetectName("", "", "rms-host-7.7.3.exe") == "rms host",
    "package display name prefers product metadata and falls back to installer filename");
var sharedSnapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "SHARED", 20, 16UL << 30, 8UL << 30, [], [], CpuTemperatureC: 90);
var sharedReport = JsonSerializer.SerializeToNode(sharedSnapshot)!.AsObject();
sharedReport["DiagnosticResult"] = JsonSerializer.SerializeToNode(DiagnosticEngine.Evaluate(sharedSnapshot));
sharedReport["DiagnosticIssues"] = JsonSerializer.SerializeToNode(Array.Empty<UserIssue>()); // stale client projection must not override raw inputs
using (var sharedDocument = JsonDocument.Parse(sharedReport.ToJsonString()))
    Check(Monitoring.CurrentIssues(sharedDocument.RootElement).Count == DiagnosticEngine.Evaluate(sharedSnapshot).Issues.Count,
        "versioned reports are evaluated by the same client/server core from raw inputs");

var workspaceRoot = sourceRoot;
foreach (var (installer, expectedVersion) in new[]
         {
             (Path.Combine(workspaceRoot, "ITSeti.Maintenance", "dist", "ITSeti-Maintenance-Setup-2.3.9.exe"), new Version(2, 3, 9)),
             (Path.Combine(workspaceRoot, "ITSeti.Maintenance", "dist", "Win7", "ITSeti-Maintenance-Win7-Setup-2.3.9.exe"), new Version(2, 3, 9)),
             (Path.Combine(workspaceRoot, "ITSeti.Maintenance", "dist", "ITSeti-Maintenance-Setup-2.4.1.exe"), new Version(2, 4, 1)),
             (Path.Combine(workspaceRoot, "ITSeti.Maintenance", "dist", "Win7", "ITSeti-Maintenance-Win7-Setup-2.4.1.exe"), new Version(2, 4, 1))
         }.Where(item => File.Exists(item.Item1)))
{
    var metadata = PackageVersionDetector.Detect(installer, Path.GetFileName(installer));
    Check(metadata.Name.StartsWith("ИТ-Сети Обслуживание ПК", StringComparison.Ordinal) && metadata.Version == expectedVersion,
        "real setup metadata auto-detects product name and ProductVersion: " + Path.GetFileName(installer));
}
var testHostExecutable = Path.Combine(AppContext.BaseDirectory, "ITSeti.Server.Tests.exe");
if (File.Exists(testHostExecutable))
{
    var hostInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(testHostExecutable);
    var expectedText = string.IsNullOrWhiteSpace(hostInfo.ProductVersion) ? hostInfo.FileVersion : hostInfo.ProductVersion;
    if (PackageVersionDetector.TryParse(expectedText, out var expectedHostVersion))
    {
        var extensionlessHost = Path.Combine(Path.GetTempPath(), "itseti-package-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(testHostExecutable, extensionlessHost);
            var metadata = PackageVersionDetector.Detect(extensionlessHost, "setup.exe");
            Check(metadata.Version == expectedHostVersion, "package metadata is detected when the server's stored blob has no extension");
        }
        finally { File.Delete(extensionlessHost); }
    }
}
Check(!PackageVersionDetector.TryParse("latest", out _), "application package upload rejects undetectable versions");
Check(Monitoring.CurrentIssues(Report("""{"Findings":["HWiNFO критично: CPU 90 °C."]}""")).Single().Severity==1,
    "Win7 HWiNFO critical findings are classified as critical");
Check(Monitoring.NewIssues(Report("""{"Findings":["HWiNFO критично: CPU 92 °C."]}"""),Report("""{"Findings":["HWiNFO критично: CPU 90 °C."]}""")).Count==0,
    "Win7 HWiNFO reading changes do not spam monitoring");

Check(OkdeskWebClient.ReadCreatedIssueId(Report("""{"redirect":{"redirect_path":"/issues/24447"}}""")) == 24447,
    "web creation reads the confirmed sequential ticket number");
Check(TicketServices.FormatComputerInfo("Инв. номер: 2041\nRMS: 123") == "Заявка из приложения\n\nДанные ПК:\nИнв. номер: 2041\nRMS: 123" &&
    TicketServices.FormatComputerInfo(null) is null && TicketServices.FormatComputerInfo(" ") is null,
    "computer details are formatted as a separate application-signed message");
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
    Check(await web.CreateIssueAsync(TicketServices.Build("Fixture", "first\r\nsecond\n<third>", "525", 123, 456) with { EquipmentIds = [501] }, CancellationToken.None) == 731,
        "web transport creates a ticket after isolated authentication");
    Check(webFixture.Issue.GetProperty("company_id").GetInt64() == 123 && webFixture.Issue.GetProperty("maintenance_entity_id").GetInt64() == 456 &&
        webFixture.Issue.GetProperty("group_id").GetInt64() == 99 && webFixture.Issue.GetProperty("description").GetString() == "first<br />second<br />&lt;third&gt;",
        "web payload preserves assignment, service defaults, multiline text and HTML encoding");
    Check(webFixture.Issue.GetProperty("equipment_ids")[0].GetInt64() == 501, "web ticket includes synchronized equipment");
    Check(await web.AddCommentAsync(731, "first\r\nsecond & <third>", [], CancellationToken.None) == 42 && webFixture.PublicReply &&
        webFixture.CommentContent == "first<br />second &amp; &lt;third&gt;",
        "web comment preserves multiline text and confirms public delivery");
    Check((await web.GetCommentsAsync(731, CancellationToken.None)).Select(x => x.Id).SequenceEqual([42L]),
        "web history pagination excludes private replies");
    typeof(OkdeskWebClient).GetField("authenticatedUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(web, DateTimeOffset.MinValue);
    await web.GetCommentsAsync(731, CancellationToken.None);
    Check(webFixture.LoginCount == 1, "expired local auth cache reuses valid web session without visiting login again");
    Check((await web.GetIssueStatusAsync(731, CancellationToken.None))!.WorkflowState == "cancelled", "incoming web cancellation status is parsed independently of comments");
    var file = (await web.GetCommentsAsync(731, CancellationToken.None)).Single().Attachments!.Single();
    Check(file.Id == 77 && (await web.DownloadAttachmentAsync(731, file, CancellationToken.None)).Length == 4,
        "public attachment metadata and bounded download are supported; private attachments are excluded");
}

var identity = DeviceIdentity.CreateFingerprint("  SN-1234 ", "11111111-1111-1111-1111-111111111111");
var webCompany = OkdeskWebClient.Normalize(Report("""{"id":"1014927","attributes":{"sequential_id":92,"name":"Fixture","active":true}}"""), true);
Check(webCompany.GetProperty("id").GetInt64() == 1014927, "web catalog preserves internal company IDs and existing device assignments");
var webSite = OkdeskWebClient.Normalize(Report("""{"id":"906876","attributes":{"name":"Office","isDisabled":true,"company":{"id":718775},"address":"Fixture address"}}"""), false);
Check(webSite.GetProperty("company_id").GetInt64() == 718775 && !webSite.GetProperty("active").GetBoolean(), "web catalog maps object owner and disabled state");
var webEquipment = OkdeskWebClient.NormalizeEquipment(Report("""{"id":"88","attributes":{"name":"PC","inventory_number":" 00-2041 ","serial_number":"SN88","company":{"id":12},"maintenance_entity":{"id":34}}}"""));
Check(webEquipment.GetProperty("company_id").GetInt64() == 12 && webEquipment.GetProperty("maintenance_entity_id").GetInt64() == 34 &&
    InventoryNumbers.Normalize(webEquipment.GetProperty("inventory_number").GetString()) == "2041",
    "web equipment parser reads company/object and normalizes formatted inventory numbers");
Check(InventoryNumbers.Normalize("IT-00 2041") == "IT002041" && InventoryNumbers.Normalize("000") is null,
    "inventory normalization ignores separators while preserving nonnumeric identifiers");
Check(InventoryNumbers.Normalize("PC01000004732") == InventoryNumbers.Normalize("4732") &&
    InventoryNumbers.Normalize("PC01000000000") is null,
    "Okdesk PC-prefixed inventory IDs match a short client inventory number without matching placeholders");
Check(DeviceDisplayFilter.IsExcluded(" RT ") && DeviceDisplayFilter.IsExcluded("srv") &&
    DeviceDisplayFilter.IsExcluded("SRV-01") && DeviceDisplayFilter.IsExcluded("SRV02000000484") &&
    DeviceDisplayFilter.IsExcluded("RT01000000484") && !DeviceDisplayFilter.IsExcluded("RTR") &&
    !DeviceDisplayFilter.IsExcluded("PC-RT"),
    "server display filter hides RT/SRV computer and inventory prefixes without matching unrelated names");
var serverProgram = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "Program.cs"));
var portalOperationsSource = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "PortalOperations.cs"));
var uploadReaderSource = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "PackageUploadReader.cs"));
var adminScriptSource = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "wwwroot", "app.js"));
var adminStylesSource = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "wwwroot", "app.css"));
var ticketCommentSync = File.ReadAllText(Path.Combine(workspaceRoot, "ITSeti.Server", "OkdeskCommentSyncService.cs"));
Check(serverProgram.Contains("admin.MapGet(\"/computer-tree\"", StringComparison.Ordinal) &&
    !portalOperationsSource.Contains("MapGet(\"/computer-tree\"", StringComparison.Ordinal) &&
    serverProgram.Contains("DeviceDisplayFilter.OkdeskEquipmentSqlPredicate", StringComparison.Ordinal) &&
    serverProgram.Split("DeviceDisplayFilter.SqlPredicate", StringSplitOptions.None).Length - 1 >= 12 &&
    portalOperationsSource.Split("DeviceDisplayFilter.SqlPredicate", StringSplitOptions.None).Length - 1 >= 3,
    "server has one computer-tree route and excludes RT/SRV from lists, counts, tickets, conversations, runs and monitoring");
Check(adminScriptSource.Contains("deviceTable.classList.add('tree-view')", StringComparison.Ordinal) &&
    adminScriptSource.Contains("deviceTable.classList.add('computer-view')", StringComparison.Ordinal) &&
    Enumerable.Range(1, 5).All(column => adminStylesSource.Contains($".device-table.computer-view th:nth-child({column})", StringComparison.Ordinal)) &&
    Enumerable.Range(1, 3).All(column => adminStylesSource.Contains($".device-table.tree-view th:nth-child({column})", StringComparison.Ordinal)),
    "computer table columns use explicit widths for both hierarchy and five-column device views");
Check(serverProgram.Contains("admin.MapGet(\"/computer-tree\"", StringComparison.Ordinal) &&
    serverProgram.Contains("PackageUploadReader.ReadAsync(context.Request, destination, MaxPackageBytes", StringComparison.Ordinal) &&
    !serverProgram.Contains("context.Request.ReadFormAsync(context.RequestAborted)", StringComparison.Ordinal) &&
    serverProgram.Contains("RequireRateLimiting(\"package-upload\")", StringComparison.Ordinal) &&
    serverProgram.Contains("Failed to remove incomplete package upload", StringComparison.Ordinal) &&
    uploadReaderSource.Contains("IncrementalHash.CreateHash(HashAlgorithmName.SHA256)", StringComparison.Ordinal) &&
    uploadReaderSource.Contains("64 * 1024", StringComparison.Ordinal),
    "server exposes the computer tree and streams package uploads with bounded memory and SHA-256");

var uploadTestRoot = Path.Combine(Path.GetTempPath(), "itseti-package-upload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(uploadTestRoot);
try
{
    const string uploadBoundary = "itseti-upload-test-boundary";
    static byte[] BuildMultipart(string boundary, params (string Name, string? FileName, string Value)[] parts)
    {
        var body = new StringBuilder();
        foreach (var part in parts)
        {
            body.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(part.Name).Append('"');
            if (part.FileName is not null) body.Append("; filename=\"").Append(part.FileName).Append('"');
            body.Append("\r\n\r\n").Append(part.Value).Append("\r\n");
        }
        body.Append("--").Append(boundary).Append("--\r\n");
        return Encoding.UTF8.GetBytes(body.ToString());
    }
    static Microsoft.AspNetCore.Http.DefaultHttpContext MultipartContext(string boundary, byte[] body)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body, writable: false);
        return context;
    }

    var validBody = BuildMultipart(uploadBoundary,
        ("key", null, "application"), ("version", null, "2.4.1"), ("name", null, "ITSeti"),
        ("platform", null, "windows"), ("file", @"C:\fakepath\setup.exe", "package-bytes"));
    var validPath = Path.Combine(uploadTestRoot, "valid-package");
    var uploaded = await PackageUploadReader.ReadAsync(MultipartContext(uploadBoundary, validBody).Request, validPath, 1024, CancellationToken.None);
    Check(uploaded.Key == "application" && uploaded.Version == "2.4.1" && uploaded.Name == "ITSeti" && uploaded.Platform == "windows" &&
        uploaded.FileName == "setup.exe" && uploaded.Length == Encoding.UTF8.GetByteCount("package-bytes") &&
        uploaded.Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("package-bytes"))) &&
        File.ReadAllText(validPath) == "package-bytes",
        "streamed package upload preserves fields, strips client path, writes bytes and calculates SHA-256");

    var oversizedPath = Path.Combine(uploadTestRoot, "oversized-package");
    try
    {
        await PackageUploadReader.ReadAsync(MultipartContext(uploadBoundary, validBody).Request, oversizedPath, 4, CancellationToken.None);
        throw new Exception("oversized package was accepted");
    }
    catch (InvalidDataException) { Check(!File.Exists(oversizedPath), "streamed package upload enforces the byte limit and removes partial files"); }

    var duplicateBody = BuildMultipart(uploadBoundary, ("file", "one.bin", "a"), ("file", "two.bin", "b"));
    var duplicatePath = Path.Combine(uploadTestRoot, "duplicate-package");
    try
    {
        await PackageUploadReader.ReadAsync(MultipartContext(uploadBoundary, duplicateBody).Request, duplicatePath, 1024, CancellationToken.None);
        throw new Exception("duplicate file fields were accepted");
    }
    catch (InvalidDataException) { Check(true, "streamed package upload rejects duplicate file parts"); }

    var longFieldBody = BuildMultipart(uploadBoundary, ("key", null, new string('x', 1025)), ("file", "one.bin", "a"));
    try
    {
        await PackageUploadReader.ReadAsync(MultipartContext(uploadBoundary, longFieldBody).Request, Path.Combine(uploadTestRoot, "long-field"), 1024, CancellationToken.None);
        throw new Exception("oversized metadata field was accepted");
    }
    catch (InvalidDataException) { Check(true, "streamed package upload bounds multipart metadata fields"); }
}
finally { Directory.Delete(uploadTestRoot, true); }
Check(ticketCommentSync.Contains("workflow_state NOT IN ('completed','cancelled','canceled','closed','resolved')", StringComparison.Ordinal),
    "Okdesk comment sync excludes completed and closed tickets from periodic polling");
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
Check(AutomaticIpEnrollment.Normalize(System.Net.IPAddress.Parse("::ffff:192.0.2.15")) == "192.0.2.15",
    "automatic enrollment normalizes proxy IPv4-mapped addresses");
Check(AutomaticIpEnrollment.ResolveUnique([new(10, 20), new(10, 20)]) == new IpAssignment(10, 20),
    "one IP assignment can be reused for a new hardware fingerprint");
Check(AutomaticIpEnrollment.ResolveUnique([new(10, 20), new(10, 21)]) is null,
    "an IP shared by multiple objects never chooses an assignment silently");
Check(AutomaticIpEnrollment.CanReuseExisting("::ffff:192.0.2.15", "192.0.2.15") &&
    AutomaticIpEnrollment.CanReuseExisting("192.0.2.15", "192.0.2.16") &&
    !AutomaticIpEnrollment.CanReuseExisting("192.0.2.15", "0.0.0.0"),
    "known hardware can reconnect after a valid DHCP address change");
Check(Monitoring.RmsChangeIssue("12345", "67890")?.Category == "identity" &&
    Monitoring.RmsChangeIssue("12345", "12345") is null && Monitoring.RmsChangeIssue(null, "67890") is null,
    "RMS changes are reported only after a known baseline");
Check(Monitoring.DuplicateInventoryIssue("INV-1", ["PC-OTHER"]).Category == "identity",
    "duplicate inventory identifiers create a monitoring issue");

var first = Report("""
    {"Notes":["CPU: температура 78 °C достигла порога 75 °C."],"Full":{"Events":[
      {"Log":"System","Provider":"Disk","Id":7,"Level":2,"RecordId":10,"Message":"Bad block"}]}}
    """);
Check(Monitoring.NewIssues(first, null).Count == 0, "first report establishes a baseline");

var initialServiceAlert = Report("""
    {"KindLabel":"Постоянный мониторинг","DiagnosticIssues":[
      {"Title":"Критическое отклонение шины 12 В","Detail":"Напряжение 6.63 В","Severity":"Critical"}]}
    """);
Check(Monitoring.NewIssues(initialServiceAlert, null).Single().Severity == 1,
    "a service-confirmed critical alert is not dropped when it is the device's first server report");
var identityFirst = Report("""{"ServerIdentityIssues":[{"Identity":"rms-id","Title":"Изменился RMS","Detail":"old · new"}]}""");
Check(Monitoring.NewIssues(identityFirst, null).Single().Category == "identity",
    "server-discovered identity conflicts alert even on the first report");
Check(Monitoring.NewIssues(Report("""
    {"KindLabel":"Проверка","DiagnosticIssues":[
      {"Title":"Историческая проблема","Detail":"Базовое состояние","Severity":"Critical"}]}
    """), null).Count == 0, "the first ordinary diagnostic report remains a baseline");

var repeated = Report("""
    {"Notes":["CPU: температура 80 °C достигла порога 75 °C."],"Full":{"Events":[
      {"Log":"System","Provider":"Disk","Id":7,"Level":2,"RecordId":11,"Message":"Bad block again"}]}}
    """);
Check(Monitoring.NewIssues(repeated, first).Count == 0, "changing readings and record IDs do not repeat alerts");

var oldReadings = Report("""{"Notes":["Наработка после последнего запуска: 61 ч.","C:\\ свободно 8,1 ГБ"]}""");
var newReadings = Report("""{"Notes":["Наработка после последнего запуска: 63 ч.","C:\\ свободно 4,2 ГБ"]}""");
Check(Monitoring.NewIssues(newReadings, oldReadings).Count == 0, "low disk space stays in reports and never enters monitoring");
var legacyResource = Report("""{"KindLabel":"Постоянный мониторинг","DiagnosticIssues":[{"Title":"Критическая загрузка CPU","Detail":"100%","Severity":"Critical"},{"Title":"Критическая загрузка памяти","Detail":"99%","Severity":"Critical"}]}""");
Check(Monitoring.NewIssues(legacyResource, null).Count == 0 && Monitoring.CurrentIssues(legacyResource).All(issue => issue.Severity == 2),
    "old clients cannot promote CPU or memory pressure to critical monitoring alerts");

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
Check(Monitoring.AlertsForReport(declared, null).Count == 2,
    "first report critical findings enter monitoring without waiting for a change");
Check(Monitoring.AlertsForReport(declared, declared).Count == 2,
    "repeated critical findings refresh monitoring instead of disappearing behind the baseline");
Check(Monitoring.AlertsForReport(win7New, null).Count == 0,
    "historical Windows events still establish a baseline before monitoring");
Check(Monitoring.AlertsForReport(legacyResource, legacyResource).Count == 2 && Monitoring.AlertsForReport(legacyResource, legacyResource).All(issue => issue.Severity == 2),
    "repeated resource pressure enters monitoring as warnings, outside the default critical filter");
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
    await policy.AllowAsync("agent.exe", "Trusted Publisher", verifiedPublisher: true);
    Check(policy.IsAllowed(Report("""{"Name":"agent","Signature":"Valid","Publisher":"Trusted Publisher"}""")) &&
        policy.Read().AllowedNames.Contains("agent", StringComparer.OrdinalIgnoreCase) &&
        policy.Read().AllowedPublishers.Contains("Trusted Publisher", StringComparer.OrdinalIgnoreCase),
        "authenticated client approval adds process name and verified publisher to persistent server policy");
    await policy.AllowAsync("unsigned-tool.exe", "Unverified Publisher", verifiedPublisher: false);
    Check(policy.Read().AllowedNames.Contains("unsigned-tool", StringComparer.OrdinalIgnoreCase) &&
        !policy.Read().AllowedPublishers.Contains("Unverified Publisher", StringComparer.OrdinalIgnoreCase),
        "unverified process approval never creates a publisher-wide exception");
    Check(policy.IsAllowed(Report("""{"Name":"fixture","Signature":"Valid","Publisher":"Microsoft Corporation"}""")) &&
        !policy.IsAllowed(Report("""{"Name":"fixture","Signature":"NotSigned","Publisher":"Microsoft Corporation"}""")),
        "publisher exceptions require a valid signature");
    var restored = new ProcessPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DataRoot"]=policyRoot }).Build());
    Check(restored.Read().AllowedNames.Contains("chrome.exe", StringComparer.OrdinalIgnoreCase) &&
        restored.Read().AllowedNames.Contains("agent", StringComparer.OrdinalIgnoreCase) &&
        restored.Read().AllowedNames.Contains("unsigned-tool", StringComparer.OrdinalIgnoreCase) &&
        restored.Read().AllowedPublishers.Contains("Trusted Publisher", StringComparer.OrdinalIgnoreCase),
        "process names and trusted publishers survive restart");
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
ticket = ticket with { EquipmentIds = [501, 502] };
Check(await client.CreateIssueAsync(ticket, CancellationToken.None) == 731, "Okdesk issue ID is read");
Check(handler.Path == "/api/v1/issues/" && handler.Query.Contains("api_token=test-token") &&
    handler.Body.Contains("\"company_id\":12") && handler.Body.Contains("\"service_type\":\"525\"") &&
    !handler.Body.Contains("maintenance_entity_id"), "Okdesk API request has the expected fields");
Check(JsonDocument.Parse(handler.Body).RootElement.GetProperty("issue").GetProperty("equipment_ids").EnumerateArray().Select(x => x.GetInt64()).SequenceEqual([501L,502L]),
    "JSON API associates equipment with the ticket");
await client.CreateIssueAsync(ticket, CancellationToken.None, [new TicketAttachment(Guid.NewGuid(), "fixture.txt", "text/plain", [1,2,3])]);
Check(handler.Body.Contains("issue[equipment_ids][]") && handler.Body.Contains("501") && handler.Body.Contains("502"),
    "multipart API preserves equipment when a ticket has files");

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
    public int LoginCount { get; private set; }
    public JsonElement Issue { get; private set; }
    public bool PublicReply { get; private set; }
    public string? CommentContent { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        string json;
        switch (path)
        {
            case "/web_api/layouts/auth": json = """{"csrfToken":"fixture"}"""; break;
            case "/web_api/sessions": LoginCount++; json = "{}"; break;
            case "/web_api/layouts/app": json = """{"csrfToken":"fixture","currentUser":{"id":10,"email":"fixture@example.test"}}"""; break;
            case "/issues/create_forms/new":
                json = JsonSerializer.Serialize(new { html_content = new { append = new Dictionary<string,string> { ["form"] = "<div collection_url='/collections/assignees?option_id=fixture' selected_text='Support'></div>" } }, options = new {type_id=1,priority_id=2} }); break;
            case "/collections/assignees?option_id=fixture":
                json = JsonSerializer.Serialize(new { html_content = new { html = new Dictionary<string,string> { ["groups"] = "<div name='group_id' value='99'><span data-text-value='Техническая поддержка'></span></div>" } } }); break;
            case "/issues":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))) Issue = body.RootElement.GetProperty("issue").Clone();
                json = """{"redirect":{"redirect_path":"/issues/731"}}"""; break;
            case "/issues/731/history_events/comments":
                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                {
                    PublicReply = body.RootElement.GetProperty("is_public").GetBoolean();
                    CommentContent = body.RootElement.GetProperty("content").GetString();
                }
                json = """{"type":"published","record":{"record":{"commentId":42}}}"""; break;
            case "/issues/731/history_events":
                json = """{"isLastPage":false,"historyEvents":[{"record":{"id":101,"commentId":43,"type":"AddCommentEvent","state":"published","isPublic":false}}]}"""; break;
            case "/issues/731/history_events?event_id=101":
                json = """{"isLastPage":true,"historyEvents":[{"record":{"id":100,"commentId":42,"type":"AddCommentEvent","state":"published","isPublic":true,"content":"answer","author":{"type":"Employee","id":10},"attachments":[{"record":{"id":77,"isPublic":true,"fileName":"note.txt","contentType":"text/plain","size":4}},{"record":{"id":78,"isPublic":false}}]}}]}"""; break;
            case "/issues/731/statuses?activity_source=card&status_holder_id=itseti-sync":
                json = JsonSerializer.Serialize(new { html_content = new { html = new Dictionary<string,string> { ["current"] = "<a class='current-status'><span class='status-text'>Отменена</span></a><span class='status-text'>Открыть</span>" } } }); break;
            case "/issues/731/history_events/attachments/77/downloads":
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("test")) };
            default: throw new Exception("Unexpected web endpoint: " + path);
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
