using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using ITSeti.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings__Database is required.");
var dataRoot = builder.Configuration["DataRoot"] ?? "/data";
var packageRoot = Path.Combine(dataRoot, "packages");
Directory.CreateDirectory(packageRoot);
Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));

var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<PasswordHasher<PortalAdmin>>();
builder.Services.AddSingleton<OkdeskSyncService>();
builder.Services.AddSingleton<OkdeskApiClient>();
builder.Services.AddSingleton<OkdeskWebClient>();
builder.Services.AddSingleton<IOkdeskClient>(services =>
    string.Equals(builder.Configuration["OkdeskTransport"], "web", StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<OkdeskWebClient>() : services.GetRequiredService<OkdeskApiClient>());
builder.Services.AddSingleton<ProcessPolicy>();
builder.Services.AddSingleton<EnrollmentDiagnostics>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OkdeskSyncService>());
builder.Services.AddHostedService<TicketDispatchService>();
builder.Services.AddHostedService<OkdeskCommentSyncService>();
builder.Services.AddHostedService<TicketActionDispatcher>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")))
    .SetApplicationName("ITSeti.Server");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "ITSeti.Admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("registration", limiter =>
    {
        limiter.PermitLimit = 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddPolicy("ticket", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers[DeviceKeys.IdHeader].ToString(), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        }));
});
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);

var app = builder.Build();
await Database.InitializeAsync(dataSource, builder.Configuration, app.Services.GetRequiredService<PasswordHasher<PortalAdmin>>());
await MonitoringHistory.ReconcileAsync(dataSource);
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    var traced = context.Request.Path == "/api/v1/enroll" || context.Request.Path == "/api/v1/assignment";
    if (!traced) { await next(); return; }
    var diagnostics = context.RequestServices.GetRequiredService<EnrollmentDiagnostics>();
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    try
    {
        await next();
        if (!context.Items.ContainsKey(nameof(EnrollmentDiagnostics)))
            diagnostics.Record(context, context.Response.StatusCode,
                context.Response.StatusCode < 400 ? "completed" : "request_rejected_before_or_during_handler");
    }
    catch (Exception ex)
    {
        diagnostics.Record(context, ex is BadHttpRequestException bad ? bad.StatusCode : 500, ex.GetType().Name);
        throw;
    }
});
app.Use(async (context, next) =>
{
    var limit = context.Request.Path.StartsWithSegments("/api/v1/check-runs")
        ? 12L * 1024 * 1024
        : context.Request.Path.StartsWithSegments("/api/v1/tickets") ? 16L * 1024 * 1024
        : context.Request.Path.StartsWithSegments("/api/admin/packages") ? 1024L * 1024 * 1024 : (long?)null;
    var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (limit.HasValue && feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit.Value;
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Content(LoginPage(antiforgery.GetAndStoreTokens(context).RequestToken!), "text/html; charset=utf-8"));
app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery, NpgsqlDataSource db,
    PasswordHasher<PortalAdmin> hasher) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest("Обновите страницу входа."); }

    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT password_hash FROM portal_admins WHERE username = @name", connection);
    command.Parameters.AddWithValue("name", username);
    var hash = await command.ExecuteScalarAsync() as string;
    var user = new PortalAdmin(username);
    if (hash is null || hasher.VerifyHashedPassword(user, hash, password) == PasswordVerificationResult.Failed)
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
        return Results.Content(LoginPage(token, "Неверный логин или пароль."), "text/html; charset=utf-8", statusCode: 401);
    }

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "Admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).RequireRateLimiting("login");

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapGet("/", async (HttpContext context, IAntiforgery antiforgery) =>
{
    var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
    var html = await File.ReadAllTextAsync(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html"));
    return Results.Content(html.Replace("__CSRF_TOKEN__", HtmlEncoder.Default.Encode(token), StringComparison.Ordinal), "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapPost("/api/v1/catalog", async (HttpRequest request, NpgsqlDataSource db, IConfiguration configuration) =>
{
    if (!RegistrationPassword.Verify(request, configuration)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    var companies = await Database.ReadCatalogAsync(connection, "SELECT id,name,active FROM companies WHERE active ORDER BY name", "company");
    var sites = await Database.ReadCatalogAsync(connection, "SELECT id,name,address,company_id AS parent_id FROM service_objects WHERE active ORDER BY name", "site");
    return Results.Ok(new { companies, sites });
}).RequireRateLimiting("registration");

app.MapPost("/api/v1/enroll", async (HttpRequest request, EnrollRequest body, NpgsqlDataSource db, IConfiguration configuration, EnrollmentDiagnostics diagnostics) =>
{
    if (!RegistrationPassword.Verify(request, configuration))
        return diagnostics.Reject(request.HttpContext, 401, "registration_password_rejected", "Неверный пароль подключения.");
    if (string.IsNullOrWhiteSpace(body.ComputerName) || body.CompanyId <= 0 || body.SiteId is <= 0)
        return diagnostics.Reject(request.HttpContext, 400, "invalid_assignment_fields", "Укажите имя компьютера и компанию.", body);
    var fingerprint = DeviceIdentity.CreateFingerprint(body.SerialNumber, body.HardwareUuid);
    if (fingerprint is null)
        return diagnostics.Reject(request.HttpContext, 400, "hardware_identity_unusable", "Не удалось определить серийный номер или аппаратный UUID. Компьютер не зарегистрирован, чтобы не создавать дубли.", body);

    await using var connection = await db.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using (var validate = new NpgsqlCommand("""
        SELECT true WHERE EXISTS(SELECT 1 FROM companies WHERE id=@company AND active)
        AND (@site IS NULL OR EXISTS(SELECT 1 FROM service_objects WHERE id=@site AND company_id=@company AND active))
        """, connection, transaction))
    {
        validate.Parameters.AddWithValue("company", body.CompanyId);
        validate.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)body.SiteId ?? DBNull.Value;
        if (await validate.ExecuteScalarAsync() is null)
            return diagnostics.Reject(request.HttpContext, 400, "company_site_missing_inactive_or_mismatch", "Компания или объект недоступны.", body);
    }

    var id = Guid.NewGuid();
    var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var supportKey = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    await using (var insert = new NpgsqlCommand("""
        INSERT INTO devices(id,computer_name,serial_number,hardware_fingerprint,inventory_number,company_id,service_object_id,api_key_hash,support_key_hash,created_at,last_seen_at)
        VALUES(@id,@name,@serial,@fingerprint,@inventory,@company,@site,@key,@support,now(),now())
        ON CONFLICT (hardware_fingerprint) WHERE hardware_fingerprint IS NOT NULL
        DO UPDATE SET computer_name=EXCLUDED.computer_name,
            serial_number=COALESCE(NULLIF(EXCLUDED.serial_number,''),devices.serial_number),
            inventory_number=EXCLUDED.inventory_number,company_id=EXCLUDED.company_id,
            service_object_id=EXCLUDED.service_object_id,api_key_hash=EXCLUDED.api_key_hash,
            support_key_hash=EXCLUDED.support_key_hash,last_seen_at=now()
        RETURNING id
        """, connection, transaction))
    {
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("name", body.ComputerName.Trim());
        insert.Parameters.AddWithValue("serial", (object?)body.SerialNumber?.Trim() ?? DBNull.Value);
        insert.Parameters.Add("fingerprint", NpgsqlDbType.Text).Value = (object?)fingerprint ?? DBNull.Value;
        insert.Parameters.AddWithValue("inventory", (object?)body.InventoryNumber ?? DBNull.Value);
        insert.Parameters.AddWithValue("company", body.CompanyId);
        insert.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)body.SiteId ?? DBNull.Value;
        insert.Parameters.AddWithValue("key", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        insert.Parameters.AddWithValue("support", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(supportKey))));
        id = (Guid)(await insert.ExecuteScalarAsync())!;
    }
    await transaction.CommitAsync();
    diagnostics.Record(request.HttpContext, 200, "enrolled_or_existing_device_reused", body);
    return Results.Ok(new { deviceId = id, deviceKey = key, supportKey });
}).RequireRateLimiting("registration");

app.MapPost("/api/v1/support-key", async (HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand(
        "UPDATE devices SET support_key_hash=@hash WHERE id=@id", connection);
    command.Parameters.AddWithValue("id", deviceId);
    command.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    await command.ExecuteNonQueryAsync();
    return Results.Ok(new { deviceId, supportKey = key });
});

app.MapGet("/api/v1/assignment", async (HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT d.company_id,d.service_object_id,c.name,s.name
        FROM devices d JOIN companies c ON c.id=d.company_id
        LEFT JOIN service_objects s ON s.id=d.service_object_id WHERE d.id=@id
        """, connection);
    command.Parameters.AddWithValue("id", deviceId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();
    return Results.Ok(new { companyId = reader.GetInt64(0), siteId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
        company = reader.GetString(2), site = reader.IsDBNull(3) ? null : reader.GetString(3) });
});

app.MapPut("/api/v1/assignment", async (HttpRequest request, UpdateAssignmentRequest body, NpgsqlDataSource db, EnrollmentDiagnostics diagnostics) =>
{
    if (!await DeviceKeys.AuthenticateAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    if (body.CompanyId <= 0 || body.SiteId is <= 0)
        return diagnostics.Reject(request.HttpContext, 400, "invalid_assignment_fields", "Выберите компанию и объект из списка.");
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        UPDATE devices SET company_id=@company,service_object_id=@site,inventory_number=@inventory
        WHERE id=@id AND EXISTS(SELECT 1 FROM companies WHERE id=@company AND active)
          AND (@site IS NULL OR EXISTS(SELECT 1 FROM service_objects WHERE id=@site AND company_id=@company AND active))
        """, connection);
    command.Parameters.AddWithValue("id", deviceId);
    command.Parameters.AddWithValue("company", body.CompanyId);
    command.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)body.SiteId ?? DBNull.Value;
    command.Parameters.Add("inventory", NpgsqlDbType.Text).Value = (object?)body.InventoryNumber ?? DBNull.Value;
    return await command.ExecuteNonQueryAsync() == 1 ? Results.Ok(new { updated = true })
        : diagnostics.Reject(request.HttpContext, 400, "company_site_missing_inactive_or_mismatch", "Компания или объект недоступны.");
});

app.MapGet("/api/v1/ticket-services", async (HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db)) return Results.Unauthorized();
    return Results.Ok(TicketServices.Names.Select(pair => new { code = pair.Key, name = pair.Value }));
});

app.MapGet("/api/v1/ticket-updates", async (HttpRequest request, NpgsqlDataSource db, DateTimeOffset? since, Guid? afterId) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync(request.HttpContext.RequestAborted);
    await using var clock = new NpgsqlCommand("SELECT now()", connection);
    var serverTime = new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync())!);
    await using var count = new NpgsqlCommand("SELECT count(*) FROM ticket_requests WHERE device_id=@device AND workflow_state NOT IN ('completed','cancelled')", connection);
    count.Parameters.AddWithValue("device", deviceId);
    var activeCount = (long)(await count.ExecuteScalarAsync())!;
    var messages = new List<object>();
    var cursor = serverTime;
    var cursorId = Guid.Empty;
    var hasMore = false;
    if (since.HasValue)
    {
        if (since > serverTime.AddMinutes(2)) return Results.BadRequest(new { error = "Invalid message cursor." });
        await using var query = new NpgsqlCommand("""
            SELECT m.id,m.request_id,r.title,m.created_at FROM ticket_messages m
            JOIN ticket_requests r ON r.request_id=m.request_id
            WHERE r.device_id=@device AND m.is_public AND m.direction='engineer'
              AND (m.created_at,m.id)>(@since,@after) AND m.created_at<=@until
            ORDER BY m.created_at,m.id LIMIT 101
            """, connection);
        query.Parameters.AddWithValue("device", deviceId);
        query.Parameters.AddWithValue("since", since.Value.UtcDateTime);
        query.Parameters.AddWithValue("until", serverTime.UtcDateTime);
        query.Parameters.AddWithValue("after", afterId ?? Guid.Empty);
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var created = new DateTimeOffset(reader.GetDateTime(3));
            if (messages.Count == 100) { hasMore = true; break; }
            cursor = created; cursorId = reader.GetGuid(0);
            messages.Add(new { id = reader.GetGuid(0), requestId = reader.GetGuid(1), title = reader.GetString(2), createdAt = created });
        }
    }
    // Overlap covers messages committed just after the read; the client deduplicates IDs.
    if (!hasMore) { cursor = serverTime.AddSeconds(-30); cursorId = Guid.Empty; }
    return Results.Ok(new { activeCount, cursor, cursorId, hasMore, messages });
});

app.MapGet("/api/v1/tickets", async (HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT request_id,title,description,service_code,state,okdesk_issue_id,created_at,last_error,workflow_state,okdesk_status_name
        FROM ticket_requests WHERE device_id=@device ORDER BY created_at DESC LIMIT 100
        """, connection);
    command.Parameters.AddWithValue("device", deviceId);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new
    {
        requestId = reader.GetGuid(0), title = reader.GetString(1), description = reader.GetString(2),
        service = TicketServices.Names.GetValueOrDefault(reader.GetString(3), reader.GetString(3)),
        status = reader.GetString(4), issueId = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
        createdAt = reader.GetDateTime(6), error = reader.IsDBNull(7) ? null : reader.GetString(7), workflowState = reader.GetString(8),
        remoteStatus = reader.IsDBNull(9) ? null : reader.GetString(9)
    });
    return Results.Ok(rows);
});

app.MapGet("/api/v1/tickets/{requestId:guid}/messages", async (Guid requestId, HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var ownership = new NpgsqlCommand("SELECT 1 FROM ticket_requests WHERE request_id=@id AND device_id=@device", connection);
    ownership.Parameters.AddWithValue("id", requestId);
    ownership.Parameters.AddWithValue("device", deviceId);
    if (await ownership.ExecuteScalarAsync() is null) return Results.NotFound();
    await using var messagesQuery = new NpgsqlCommand("""
        SELECT id,direction,content,state,COALESCE(okdesk_created_at,created_at),last_error FROM ticket_messages
        WHERE request_id=@id AND is_public ORDER BY COALESCE(okdesk_created_at,created_at),id LIMIT 500
        """, connection);
    messagesQuery.Parameters.AddWithValue("id", requestId);
    await using var reader = await messagesQuery.ExecuteReaderAsync();
    var messages = new List<object>();
    while (await reader.ReadAsync()) messages.Add(new
    {
        id = reader.GetGuid(0), direction = reader.GetString(1), content = reader.GetString(2),
        status = reader.GetString(3), createdAt = reader.GetDateTime(4),
        error = reader.IsDBNull(5) ? null : reader.GetString(5)
    });
    await reader.DisposeAsync();
    await using var filesQuery = new NpgsqlCommand("""
        SELECT id,message_id,file_name,content_type,octet_length(bytes) FROM ticket_attachments
        WHERE request_id=@id AND (message_id IS NULL OR EXISTS(SELECT 1 FROM ticket_messages m WHERE m.id=message_id AND m.is_public)) ORDER BY created_at,id
        """, connection);
    filesQuery.Parameters.AddWithValue("id", requestId);
    await using var fileReader = await filesQuery.ExecuteReaderAsync();
    var attachments = new List<object>();
    while (await fileReader.ReadAsync()) attachments.Add(new
    {
        id = fileReader.GetGuid(0), messageId = fileReader.IsDBNull(1) ? (Guid?)null : fileReader.GetGuid(1),
        fileName = fileReader.GetString(2), contentType = fileReader.GetString(3), size = fileReader.GetInt32(4)
    });
    return Results.Ok(new { messages, attachments });
});

app.MapGet("/api/v1/tickets/{requestId:guid}/attachments/{fileId:guid}", async (Guid requestId, Guid fileId,
    HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT a.file_name,a.content_type,a.bytes FROM ticket_attachments a
        JOIN ticket_requests t ON t.request_id=a.request_id
        WHERE a.id=@file AND a.request_id=@request AND t.device_id=@device
          AND (a.message_id IS NULL OR EXISTS(SELECT 1 FROM ticket_messages m WHERE m.id=a.message_id AND m.is_public))
        """, connection);
    command.Parameters.AddWithValue("file", fileId);
    command.Parameters.AddWithValue("request", requestId);
    command.Parameters.AddWithValue("device", deviceId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();
    request.HttpContext.Response.Headers.CacheControl = "private, no-store";
    return Results.File(reader.GetFieldValue<byte[]>(2), reader.GetString(1), reader.GetString(0));
});

app.MapGet("/api/v1/tickets/{requestId:guid}", async (Guid requestId, HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand(
        "SELECT state,okdesk_issue_id FROM ticket_requests WHERE request_id=@request AND device_id=@device", connection);
    command.Parameters.AddWithValue("request", requestId);
    command.Parameters.AddWithValue("device", deviceId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();
    return Results.Ok(new { requestId, status = reader.GetString(0), issueId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1) });
});

app.MapPost("/api/v1/tickets", async (HttpRequest request, CreateTicketRequest body, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    if (body.RequestId == Guid.Empty) return Results.BadRequest(new { error = "Укажите requestId." });

    await using var connection = await db.OpenConnectionAsync();
    long companyId;
    long? siteId;
    await using (var assignment = new NpgsqlCommand("""
        SELECT d.company_id,d.service_object_id FROM devices d
        JOIN companies c ON c.id=d.company_id AND c.active
        LEFT JOIN service_objects s ON s.id=d.service_object_id AND s.company_id=d.company_id AND s.active
        WHERE d.id=@device AND (d.service_object_id IS NULL OR s.id IS NOT NULL)
        """, connection))
    {
        assignment.Parameters.AddWithValue("device", deviceId);
        await using var reader = await assignment.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.BadRequest(new { error = "Привязка компьютера к компании или объекту недействительна." });
        companyId = reader.GetInt64(0);
        siteId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
    }

    OkdeskIssue issue;
    try { issue = TicketServices.Build(body.Title ?? "", body.Description ?? "", body.ServiceCode ?? "", companyId, siteId); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Проверьте тему, описание и сервис заявки." }); }
    IReadOnlyList<TicketAttachment> files;
    try { files = TicketAttachments.Decode(body.Attachments); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    var signature = JsonSerializer.Serialize(issue) + string.Concat(files.Select(file => file.FileName + ":" +
        Convert.ToHexString(SHA256.HashData(file.Bytes))));
    var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
    await using var transaction = await connection.BeginTransactionAsync();
    int inserted;
    await using (var reserve = new NpgsqlCommand("""
        INSERT INTO ticket_requests(request_id,device_id,company_id,service_object_id,title,description,service_code,payload_hash,state)
        VALUES(@request,@device,@company,@site,@title,@description,@service,@hash,'queued') ON CONFLICT(request_id) DO NOTHING
        """, connection, transaction))
    {
        reserve.Parameters.AddWithValue("request", body.RequestId);
        reserve.Parameters.AddWithValue("device", deviceId);
        reserve.Parameters.AddWithValue("company", companyId);
        reserve.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)siteId ?? DBNull.Value;
        reserve.Parameters.AddWithValue("title", issue.Title);
        reserve.Parameters.AddWithValue("description", issue.Description);
        reserve.Parameters.AddWithValue("service", body.ServiceCode!);
        reserve.Parameters.AddWithValue("hash", payloadHash);
        inserted = await reserve.ExecuteNonQueryAsync();
    }
    if (inserted == 0)
    {
        await transaction.RollbackAsync();
        await using var existing = new NpgsqlCommand("""
            SELECT payload_hash,state,okdesk_issue_id FROM ticket_requests
            WHERE request_id=@request AND device_id=@device
            """, connection);
        existing.Parameters.AddWithValue("request", body.RequestId);
        existing.Parameters.AddWithValue("device", deviceId);
        await using var reader = await existing.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != payloadHash)
            return Results.Conflict(new { error = "requestId уже использован для другой заявки." });
        var state = reader.GetString(1);
        return Results.Ok(new { requestId = body.RequestId, status = state,
            issueId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2) });
    }
    foreach (var file in files)
    {
        await using var attachment = new NpgsqlCommand("""
            INSERT INTO ticket_attachments(id,request_id,file_name,content_type,bytes)
            VALUES(@id,@request,@name,@type,@bytes)
            """, connection, transaction);
        attachment.Parameters.AddWithValue("id", file.Id);
        attachment.Parameters.AddWithValue("request", body.RequestId);
        attachment.Parameters.AddWithValue("name", file.FileName);
        attachment.Parameters.AddWithValue("type", file.ContentType);
        attachment.Parameters.Add("bytes", NpgsqlDbType.Bytea).Value = file.Bytes;
        await attachment.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
    return Results.Json(new { requestId = body.RequestId, status = "queued", issueId = (long?)null }, statusCode: 202);
}).RequireRateLimiting("ticket");

app.MapPost("/api/v1/tickets/{requestId:guid}/messages", async (Guid requestId, HttpRequest request,
    CreateTicketMessageRequest body, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateSupportAsync(request, db) ||
        !Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    if (body.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(body.Content) || body.Content.Trim().Length > 5000)
        return Results.BadRequest(new { error = "Введите сообщение до 5000 символов." });
    IReadOnlyList<TicketAttachment> files;
    try { files = TicketAttachments.Decode(body.Attachments); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    var content = body.Content.Trim();
    var signature = content + string.Concat(files.Select(file => file.FileName + ":" +
        Convert.ToHexString(SHA256.HashData(file.Bytes))));
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
    await using var connection = await db.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using var insert = new NpgsqlCommand("""
        INSERT INTO ticket_messages(id,request_id,direction,content,payload_hash,state)
        SELECT @id,@request,'user',@content,@hash,'queued'
        WHERE EXISTS(SELECT 1 FROM ticket_requests WHERE request_id=@request AND device_id=@device)
        ON CONFLICT(id) DO NOTHING
        """, connection, transaction);
    insert.Parameters.AddWithValue("id", body.MessageId);
    insert.Parameters.AddWithValue("request", requestId);
    insert.Parameters.AddWithValue("device", deviceId);
    insert.Parameters.AddWithValue("content", content);
    insert.Parameters.AddWithValue("hash", hash);
    var inserted = await insert.ExecuteNonQueryAsync();
    if (inserted == 0)
    {
        await transaction.RollbackAsync();
        await using var existing = new NpgsqlCommand("""
            SELECT payload_hash,state FROM ticket_messages m JOIN ticket_requests t ON t.request_id=m.request_id
            WHERE m.id=@id AND m.request_id=@request AND t.device_id=@device
            """, connection);
        existing.Parameters.AddWithValue("id", body.MessageId);
        existing.Parameters.AddWithValue("request", requestId);
        existing.Parameters.AddWithValue("device", deviceId);
        await using var reader = await existing.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.NotFound();
        return reader.GetString(0) == hash ? Results.Ok(new { messageId = body.MessageId, status = reader.GetString(1) })
            : Results.Conflict(new { error = "messageId уже использован для другого сообщения." });
    }
    foreach (var file in files)
    {
        await using var attachment = new NpgsqlCommand("""
            INSERT INTO ticket_attachments(id,request_id,message_id,file_name,content_type,bytes)
            VALUES(@id,@request,@message,@name,@type,@bytes)
            """, connection, transaction);
        attachment.Parameters.AddWithValue("id", file.Id);
        attachment.Parameters.AddWithValue("request", requestId);
        attachment.Parameters.AddWithValue("message", body.MessageId);
        attachment.Parameters.AddWithValue("name", file.FileName);
        attachment.Parameters.AddWithValue("type", file.ContentType);
        attachment.Parameters.Add("bytes", NpgsqlDbType.Bytea).Value = file.Bytes;
        await attachment.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
    return Results.Json(new { messageId = body.MessageId, status = "queued" }, statusCode: 202);
}).RequireRateLimiting("ticket");

app.MapPost("/api/v1/check-runs", async (HttpRequest request, JsonDocument report, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateAsync(request, db)) return Results.Unauthorized();
    if (Encoding.UTF8.GetByteCount(report.RootElement.GetRawText()) > 12 * 1024 * 1024)
        return Results.BadRequest(new { error = "Отчёт превышает 12 МБ." });

    if (!Guid.TryParse(request.Headers[DeviceKeys.IdHeader].ToString(), out var deviceId)) return Results.Unauthorized();
    var runId = JsonRead.Guid(report.RootElement, "Id", "id") ?? Guid.NewGuid();
    var started = (JsonRead.Date(report.RootElement, "StartedAt", "startedAt") ?? DateTimeOffset.UtcNow).ToUniversalTime();
    var machine = JsonRead.String(report.RootElement, "ComputerName", "computerName") ?? "неизвестен";
    var os = JsonRead.String(report.RootElement, "WindowsEdition", "OsName", "osName");
    var version = JsonRead.String(report.RootElement, "WindowsRelease", "OsVersion", "osVersion");
    var findings = Monitoring.CurrentIssues(report.RootElement).Where(i => i.Category == "finding")
        .Select(i => i.Detail).Distinct().ToArray();
    var inventory = JsonRead.String(report.RootElement, "InventoryNumber", "inventoryNumber");
    var rmsId = JsonRead.String(report.RootElement, "RmsId", "rmsId");
    var anyDeskId = JsonRead.String(report.RootElement, "AnyDeskId", "anyDeskId");
    var administrators = JsonRead.StringList(report.RootElement, "AdminAccounts", "adminAccounts");
    var kind = JsonRead.String(report.RootElement, "KindLabel", "Kind", "kind") ?? "Проверка";
    var summary = JsonSerializer.Serialize(new
    {
        computerName = machine, os, version, kind, startedAt = started,
        cpuPercent = JsonRead.Number(report.RootElement, "CpuPercent", "cpuPercent"),
        cpuTemperatureC = JsonRead.Number(report.RootElement, "CpuTemperatureC", "cpuTemperatureC"),
        memoryUsedPercent = JsonRead.Number(report.RootElement, "MemoryUsedPercent", "memoryUsedPercent"),
        inventory, rmsId, anyDeskId, administrators, findings,
        findingCount = findings.Length,
        result = findings.Length == 0 ? "Без замечаний" : $"Требует внимания: {findings.Length}"
    });
    var raw = report.RootElement.GetRawText();
    await using var connection = await db.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    IReadOnlyList<MonitorIssue> newIssues;
    await using (var previous = new NpgsqlCommand("""
        SELECT
          (SELECT details::text FROM check_runs WHERE device_id=@device AND started_at<@started
           ORDER BY started_at DESC,received_at DESC LIMIT 1),
          (SELECT details::text FROM check_runs WHERE device_id=@device AND started_at<@started
           AND jsonb_typeof(coalesce(details #> '{Full,Events}',details -> 'Events'))='array'
           ORDER BY started_at DESC,received_at DESC LIMIT 1)
        """, connection, transaction))
    {
        previous.Parameters.AddWithValue("device", deviceId);
        previous.Parameters.AddWithValue("started", started);
        await using var reader = await previous.ExecuteReaderAsync();
        await reader.ReadAsync();
        using var previousReport = reader.IsDBNull(0) ? null : JsonDocument.Parse(reader.GetString(0));
        using var previousEvents = reader.IsDBNull(1) ? null : JsonDocument.Parse(reader.GetString(1));
        newIssues = Monitoring.NewIssues(report.RootElement, previousReport?.RootElement, previousEvents?.RootElement, includeWarnings: true);
    }
    int inserted;
    await using (var insert = new NpgsqlCommand("""
        INSERT INTO check_runs(id,device_id,started_at,kind,summary,details)
        VALUES(@id,@device,@started,@kind,@summary,@details)
        ON CONFLICT(id) DO NOTHING
        """, connection, transaction))
    {
        insert.Parameters.AddWithValue("id", runId);
        insert.Parameters.AddWithValue("device", deviceId);
        insert.Parameters.AddWithValue("started", started);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.Add("summary", NpgsqlDbType.Jsonb).Value = summary;
        insert.Parameters.Add("details", NpgsqlDbType.Jsonb).Value = raw;
        inserted = await insert.ExecuteNonQueryAsync();
    }
    if (inserted > 0)
    {
        foreach (var issue in newIssues)
        {
            await using var alert = new NpgsqlCommand("""
                INSERT INTO monitor_alerts(device_id,run_id,category,identity,title,detail,severity)
                VALUES(@device,@run,@category,@identity,@title,@detail,@severity)
                ON CONFLICT(run_id,category,identity) DO NOTHING
                """, connection, transaction);
            alert.Parameters.AddWithValue("device", deviceId);
            alert.Parameters.AddWithValue("run", runId);
            alert.Parameters.AddWithValue("category", issue.Category);
            alert.Parameters.AddWithValue("identity", issue.Identity);
            alert.Parameters.AddWithValue("title", issue.Title);
            alert.Parameters.AddWithValue("detail", issue.Detail);
            alert.Parameters.AddWithValue("severity", issue.Severity);
            await alert.ExecuteNonQueryAsync();
        }
    }
    await using (var update = new NpgsqlCommand("""
        UPDATE devices SET computer_name=@name, os_name=@os, os_version=@version,
            inventory_number=CASE WHEN @inventory IS NULL THEN inventory_number ELSE @inventory END,
            last_seen_at=now()
        WHERE id=@id
        """, connection, transaction))
    {
        update.Parameters.AddWithValue("name", machine);
        update.Parameters.AddWithValue("os", (object?)os ?? DBNull.Value);
        update.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
        update.Parameters.Add("inventory", NpgsqlDbType.Text).Value = inventory is { Length: 4 } ? inventory : DBNull.Value;
        update.Parameters.AddWithValue("id", deviceId);
        await update.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
    return Results.Ok(new { accepted = true, reportId = runId });
});

app.MapGet("/api/v1/updates", async (HttpRequest request, NpgsqlDataSource db) =>
{
    if (!await DeviceKeys.AuthenticateAsync(request, db)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT id,package_key,version,platform,sha256,size_bytes,download_name
        FROM packages WHERE is_latest ORDER BY package_key,platform
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new
    {
        id = reader.GetGuid(0), key = reader.GetString(1), version = reader.GetString(2),
        platform = reader.GetString(3), sha256 = reader.GetString(4), sizeBytes = reader.GetInt64(5),
        fileName = reader.GetString(6)
    });
    return Results.Ok(rows);
});

app.MapGet("/api/v1/packages/{id:guid}/download", async (Guid id, HttpRequest request, NpgsqlDataSource db, HttpContext context) =>
{
    if (!await DeviceKeys.AuthenticateAsync(request, db)) return Results.Unauthorized();
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT download_name FROM packages WHERE id=@id", connection);
    command.Parameters.AddWithValue("id", id);
    var name = await command.ExecuteScalarAsync() as string;
    if (name is null) return Results.NotFound();
    var file = Path.Combine(packageRoot, id.ToString("N"));
    if (!File.Exists(file)) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, no-store";
    return Results.File(file, "application/octet-stream", Path.GetFileName(name), enableRangeProcessing: true);
});

var admin = app.MapGroup("/api/admin").RequireAuthorization();
PortalOperations.Map(admin);
admin.MapGet("/process-policy", (ProcessPolicy policy) => Results.Ok(policy.Read()));
admin.MapGet("/enrollment-diagnostics", (EnrollmentDiagnostics diagnostics) => Results.Ok(diagnostics.Read()));
admin.MapPut("/process-policy", async (ProcessPolicyDocument body, ProcessPolicy policy, HttpContext context, IAntiforgery csrf) =>
{
    await csrf.ValidateRequestAsync(context);
    try { await policy.WriteAsync(body); return Results.Ok(policy.Read()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/v1/process-policy", async (HttpRequest request, NpgsqlDataSource db, ProcessPolicy policy) =>
    await DeviceKeys.AuthenticateAsync(request, db) ? Results.Ok(policy.Read()) : Results.Unauthorized());
admin.MapGet("/tickets", async (NpgsqlDataSource db, string? status, long? companyId, long? siteId, int? page) =>
{
    if (status is not (null or "queued" or "sending" or "created" or "rejected" or "unknown"))
        return Results.BadRequest(new { error = "Неизвестный статус заявки." });
    var offset = Math.Clamp(page ?? 0, 0, 10000) * 50;
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT t.request_id,t.state,t.title,t.description,t.service_code,t.okdesk_issue_id,t.last_error,
               t.created_at,t.updated_at,d.id,d.serial_number,d.inventory_number,c.name,s.name,t.workflow_state,
               (SELECT a.state FROM ticket_actions a WHERE a.request_id=t.request_id ORDER BY a.created_at DESC,a.id DESC LIMIT 1),
               (SELECT a.last_error FROM ticket_actions a WHERE a.request_id=t.request_id ORDER BY a.created_at DESC,a.id DESC LIMIT 1)
        FROM ticket_requests t JOIN devices d ON d.id=t.device_id
        LEFT JOIN companies c ON c.id=t.company_id
        LEFT JOIN service_objects s ON s.id=t.service_object_id
        WHERE (@status IS NULL OR t.state=@status) AND (@company IS NULL OR t.company_id=@company)
          AND (@site IS NULL OR t.service_object_id=@site)
        ORDER BY t.created_at DESC,t.request_id DESC LIMIT 51 OFFSET @offset
        """, connection);
    command.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
    command.Parameters.Add("company", NpgsqlDbType.Bigint).Value = (object?)companyId ?? DBNull.Value;
    command.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)siteId ?? DBNull.Value;
    command.Parameters.AddWithValue("offset", offset);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new
    {
        requestId = reader.GetGuid(0), status = reader.GetString(1), title = reader.GetString(2),
        description = reader.GetString(3), service = TicketServices.Names.GetValueOrDefault(reader.GetString(4), reader.GetString(4)),
        issueId = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
        error = reader.IsDBNull(6) ? null : reader.GetString(6), createdAt = reader.GetDateTime(7),
        updatedAt = reader.GetDateTime(8), deviceId = reader.GetGuid(9), serial = reader.IsDBNull(10) ? null : reader.GetString(10),
        inventory = reader.IsDBNull(11) ? null : reader.GetString(11),
        company = reader.IsDBNull(12) ? null : reader.GetString(12), site = reader.IsDBNull(13) ? null : reader.GetString(13),
        workflow = reader.GetString(14), actionState = reader.IsDBNull(15) ? null : reader.GetString(15), actionError = reader.IsDBNull(16) ? null : reader.GetString(16)
    });
    return Results.Ok(new { items = rows.Take(50), hasMore = rows.Count > 50, page = offset / 50 });
});
admin.MapGet("/overview", async (NpgsqlDataSource db) =>
{
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT (SELECT count(*) FROM devices),
               (SELECT count(*) FROM devices WHERE last_seen_at > now()-interval '24 hours'),
               (SELECT count(*) FROM check_runs WHERE NOT is_test AND started_at > now()-interval '24 hours' AND started_at<=now()),
               (SELECT count(*) FROM companies WHERE active),
               (SELECT count(*) FROM service_objects WHERE active),
               (SELECT count(*) FROM monitor_alerts WHERE visible AND severity=1 AND detected_at > now()-interval '7 days')
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return Results.Ok(new { computers = reader.GetInt64(0), online24h = reader.GetInt64(1), checks24h = reader.GetInt64(2), companies = reader.GetInt64(3), sites = reader.GetInt64(4), newAlerts7d = reader.GetInt64(5) });
});
admin.MapGet("/device-options", async (NpgsqlDataSource db, string? q, long? companyId, long? siteId) =>
{
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT d.id,d.serial_number,d.inventory_number,d.os_name,d.company_id,d.service_object_id,c.name,s.name
        FROM devices d LEFT JOIN companies c ON c.id=d.company_id LEFT JOIN service_objects s ON s.id=d.service_object_id
        WHERE (@q IS NULL OR coalesce(d.serial_number,'') ILIKE '%'||@q||'%' OR coalesce(d.inventory_number,'') ILIKE '%'||@q||'%' OR d.id::text ILIKE '%'||@q||'%')
          AND (@company IS NULL OR d.company_id=@company) AND (@site IS NULL OR d.service_object_id=@site)
        ORDER BY d.serial_number NULLS LAST,d.inventory_number NULLS LAST,d.id
        LIMIT 5001
        """, connection);
    var query = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 120)];
    command.Parameters.Add("q", NpgsqlDbType.Text).Value = (object?)query ?? DBNull.Value;
    command.Parameters.Add("company", NpgsqlDbType.Bigint).Value = (object?)companyId ?? DBNull.Value;
    command.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)siteId ?? DBNull.Value;
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new
    {
        id = reader.GetGuid(0), serial = reader.IsDBNull(1) ? null : reader.GetString(1),
        inventory = reader.IsDBNull(2) ? null : reader.GetString(2), os = reader.IsDBNull(3) ? null : reader.GetString(3),
        companyId = reader.GetInt64(4), siteId = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
        company = reader.IsDBNull(6) ? null : reader.GetString(6), site = reader.IsDBNull(7) ? null : reader.GetString(7)
    });
    return Results.Ok(new { items = rows.Take(5000), hasMore = rows.Count > 5000 });
});
admin.MapGet("/devices", async (NpgsqlDataSource db, string? q, long? companyId, long? siteId, Guid? deviceId, int? page, bool? unassignedSite) =>
{
    var offset = Math.Clamp(page ?? 0, 0, 10000) * 100;
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT d.id,d.serial_number,d.inventory_number,d.os_name,d.os_version,d.last_seen_at,
               c.name,s.name,d.company_id,d.service_object_id,
               (SELECT r.started_at FROM check_runs r WHERE r.device_id=d.id ORDER BY r.started_at DESC,r.received_at DESC LIMIT 1),
               (SELECT r.details FROM check_runs r WHERE r.device_id=d.id ORDER BY r.started_at DESC,r.received_at DESC LIMIT 1)
        FROM devices d LEFT JOIN companies c ON c.id=d.company_id LEFT JOIN service_objects s ON s.id=d.service_object_id
        WHERE (@q IS NULL OR coalesce(d.serial_number,'') ILIKE '%'||@q||'%' OR coalesce(d.inventory_number,'') ILIKE '%'||@q||'%' OR d.id::text ILIKE '%'||@q||'%')
          AND (@company IS NULL OR d.company_id=@company) AND (@site IS NULL OR d.service_object_id=@site)
          AND (@device IS NULL OR d.id=@device)
          AND (NOT @unassigned OR d.service_object_id IS NULL)
        ORDER BY d.last_seen_at DESC NULLS LAST,d.id LIMIT 101 OFFSET @offset
        """, connection);
    command.Parameters.Add("q", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim()[..Math.Min(q.Trim().Length, 120)];
    command.Parameters.Add("company", NpgsqlDbType.Bigint).Value = (object?)companyId ?? DBNull.Value;
    command.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)siteId ?? DBNull.Value;
    command.Parameters.Add("device", NpgsqlDbType.Uuid).Value = (object?)deviceId ?? DBNull.Value;
    command.Parameters.AddWithValue("unassigned", unassignedSite ?? false);
    command.Parameters.AddWithValue("offset", offset);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync())
    {
        using var report = reader.IsDBNull(11) ? null : JsonDocument.Parse(reader.GetString(11));
        var issues = report is null ? null : Monitoring.CurrentIssues(report.RootElement);
        rows.Add(new {
        id = reader.GetGuid(0), serial = reader.IsDBNull(1) ? null : reader.GetString(1),
        inventory = reader.IsDBNull(2) ? null : reader.GetString(2), os = reader.IsDBNull(3) ? null : reader.GetString(3),
        osVersion = reader.IsDBNull(4) ? null : reader.GetString(4), lastSeen = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
        company = reader.IsDBNull(6) ? null : reader.GetString(6), site = reader.IsDBNull(7) ? null : reader.GetString(7),
        companyId = reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8), siteId = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9),
        lastCheck = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
        health = issues is null ? "unknown" : issues.Any(i => i.Severity == 1) ? "critical" : issues.Count > 0 ? "warning" : "normal"
        });
    }
    return Results.Ok(new { items = rows.Take(100), hasMore = rows.Count > 100, page = offset / 100 });
});
admin.MapGet("/monitoring", async (NpgsqlDataSource db, int? days, long? companyId, long? siteId, string? category, int? page, int? severity) =>
{
    var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
    var offset = Math.Clamp(page ?? 0, 0, 10000) * 100;
    if (category is not (null or "event" or "finding")) return Results.BadRequest(new { error = "Неизвестный тип события." });
    if (severity is not (null or 0 or 1 or 2)) return Results.BadRequest(new { error = "Неизвестная важность события." });
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT a.id,a.device_id,a.run_id,a.detected_at,a.category,a.title,a.detail,a.severity,
               d.serial_number,d.inventory_number,c.name,s.name
        FROM monitor_alerts a JOIN devices d ON d.id=a.device_id
        LEFT JOIN companies c ON c.id=d.company_id LEFT JOIN service_objects s ON s.id=d.service_object_id
        WHERE a.visible AND (@severity=0 OR a.severity=@severity) AND a.detected_at>=@since AND (@company IS NULL OR d.company_id=@company)
          AND (@site IS NULL OR d.service_object_id=@site) AND (@category IS NULL OR a.category=@category)
        ORDER BY a.detected_at DESC,a.id DESC LIMIT 101 OFFSET @offset
        """, connection);
    command.Parameters.AddWithValue("since", since);
    command.Parameters.AddWithValue("severity", severity ?? 1);
    command.Parameters.Add("company", NpgsqlDbType.Bigint).Value = (object?)companyId ?? DBNull.Value;
    command.Parameters.Add("site", NpgsqlDbType.Bigint).Value = (object?)siteId ?? DBNull.Value;
    command.Parameters.Add("category", NpgsqlDbType.Text).Value = (object?)category ?? DBNull.Value;
    command.Parameters.AddWithValue("offset", offset);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new
    {
        id = reader.GetInt64(0), deviceId = reader.GetGuid(1), runId = reader.GetGuid(2), detectedAt = reader.GetDateTime(3),
        category = reader.GetString(4), title = reader.GetString(5), detail = reader.GetString(6), severity = reader.GetInt16(7),
        serial = reader.IsDBNull(8) ? null : reader.GetString(8), inventory = reader.IsDBNull(9) ? null : reader.GetString(9),
        company = reader.IsDBNull(10) ? null : reader.GetString(10), site = reader.IsDBNull(11) ? null : reader.GetString(11)
    });
    return Results.Ok(new { items = rows.Take(100), hasMore = rows.Count > 100, page = offset / 100 });
});
admin.MapGet("/devices/{id:guid}/runs", async (Guid id, NpgsqlDataSource db) =>
{
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT id,started_at,kind,details FROM check_runs WHERE device_id=@id ORDER BY started_at DESC LIMIT 100", connection);
    command.Parameters.AddWithValue("id", id);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync())
    {
        using var report = JsonDocument.Parse(reader.GetString(3));
        var issues = Monitoring.CurrentIssues(report.RootElement);
        var critical = issues.Count(i => i.Severity == 1);
        var warnings = issues.Count(i => i.Severity == 2);
        rows.Add(new { id = reader.GetGuid(0), startedAt = reader.GetDateTime(1), kind = reader.GetString(2), summary = new {
            criticalCount = critical, warningCount = warnings, findingCount = issues.Count,
            result = critical > 0 ? $"Критических проблем: {critical}" : warnings > 0 ? $"Предупреждений: {warnings}" : "Всё в порядке"
        }});
    }
    return Results.Ok(rows);
});
admin.MapGet("/runs/{id:guid}", async (Guid id, NpgsqlDataSource db, ProcessPolicy policy) =>
{
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("""
        SELECT r.device_id,r.started_at,r.kind,r.details,
          (SELECT p.details FROM check_runs p WHERE p.device_id=r.device_id AND p.started_at<r.started_at ORDER BY p.started_at DESC,p.received_at DESC LIMIT 1)
        FROM check_runs r WHERE r.id=@id
        """, connection);
    command.Parameters.AddWithValue("id", id);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();
    using var details = JsonDocument.Parse(reader.GetString(3));
    var full = JsonRead.Property(details.RootElement, "Full");
    var processes = full is { ValueKind: JsonValueKind.Object } ? JsonRead.Property(full.Value, "Processes") : JsonRead.Property(details.RootElement, "Processes");
    var unknownProcesses = processes is { ValueKind: JsonValueKind.Array } ? processes.Value.EnumerateArray().Where(p => !policy.IsAllowed(p)).Select(p => p.Clone()).ToArray() : [];
    return Results.Ok(new { deviceId = reader.GetGuid(0), startedAt = reader.GetDateTime(1), kind = reader.GetString(2), details = details.RootElement.Clone(), unknownProcesses, issues = Monitoring.CurrentIssues(details.RootElement), previous = reader.IsDBNull(4) ? (JsonElement?)null : JsonDocument.Parse(reader.GetString(4)).RootElement.Clone() });
});
admin.MapGet("/catalog", async (NpgsqlDataSource db) =>
{
    await using var connection = await db.OpenConnectionAsync();
    var companies = await Database.ReadCatalogAsync(connection, "SELECT id,name,active FROM companies WHERE active ORDER BY name", "company");
    var sites = await Database.ReadCatalogAsync(connection, "SELECT id,name,address,company_id AS parent_id FROM service_objects WHERE active ORDER BY name", "site");
    return Results.Ok(new { companies, sites });
});
admin.MapPost("/okdesk/sync", async (HttpContext context, IAntiforgery antiforgery, OkdeskSyncService sync) =>
{
    if (!await SameOriginAsync(context)) return Results.BadRequest(new { error = "Недопустимый источник запроса." });
    await antiforgery.ValidateRequestAsync(context);
    var result = await sync.SyncOnceAsync(context.RequestAborted);
    return result.Success ? Results.Ok(result) : Results.Json(new { error = result.Error }, statusCode: 503);
});
admin.MapGet("/packages", async (NpgsqlDataSource db) =>
{
    await using var connection = await db.OpenConnectionAsync();
    await using var command = new NpgsqlCommand("SELECT id,package_key,version,platform,sha256,size_bytes,download_name,is_latest,created_at FROM packages ORDER BY created_at DESC LIMIT 300", connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync()) rows.Add(new { id=reader.GetGuid(0), key=reader.GetString(1), version=reader.GetString(2), platform=reader.GetString(3), sha256=reader.GetString(4), sizeBytes=reader.GetInt64(5), fileName=reader.GetString(6), latest=reader.GetBoolean(7), createdAt=reader.GetDateTime(8) });
    return Results.Ok(rows);
});
admin.MapPost("/packages/{id:guid}/activate", async (Guid id, HttpContext context, IAntiforgery antiforgery, NpgsqlDataSource db) =>
{
    if (!await SameOriginAsync(context)) return Results.BadRequest(new { error = "Недопустимый источник запроса." });
    await antiforgery.ValidateRequestAsync(context);
    await using var connection = await db.OpenConnectionAsync(context.RequestAborted);
    await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
    string? key;
    string? platform;
    await using (var find = new NpgsqlCommand("SELECT package_key,platform,storage_name FROM packages WHERE id=@id", connection, transaction))
    {
        find.Parameters.AddWithValue("id", id);
        await using var reader = await find.ExecuteReaderAsync(context.RequestAborted);
        if (!await reader.ReadAsync(context.RequestAborted)) return Results.NotFound();
        key = reader.GetString(0); platform = reader.GetString(1);
        if (!File.Exists(Path.Combine(packageRoot, reader.GetString(2)))) return Results.NotFound();
    }
    await using (var packageLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key||':'||@platform,0))",connection,transaction))
    {
        packageLock.Parameters.AddWithValue("key",key); packageLock.Parameters.AddWithValue("platform",platform);
        await packageLock.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await using (var demote = new NpgsqlCommand("UPDATE packages SET is_latest=false WHERE package_key=@key AND platform=@platform", connection, transaction))
    {
        demote.Parameters.AddWithValue("key", key); demote.Parameters.AddWithValue("platform", platform);
        await demote.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await using (var promote = new NpgsqlCommand("UPDATE packages SET is_latest=true WHERE id=@id", connection, transaction))
    {
        promote.Parameters.AddWithValue("id", id); await promote.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await transaction.CommitAsync(context.RequestAborted);
    return Results.Ok(new { active = id });
});
admin.MapPost("/packages", async (HttpContext context, IAntiforgery antiforgery, NpgsqlDataSource db) =>
{
    if (!await SameOriginAsync(context)) return Results.BadRequest(new { error = "Недопустимый источник запроса." });
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var key = form["key"].ToString().Trim().ToLowerInvariant();
    var version = form["version"].ToString().Trim();
    var platform = form["platform"].ToString().Trim().ToLowerInvariant();
    var file = form.Files.GetFile("file");
    if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-zA-Z0-9._-]{1,64}$") ||
        !System.Text.RegularExpressions.Regex.IsMatch(version, "^[a-zA-Z0-9.+_-]{1,48}$") ||
        platform is not ("windows" or "win7" or "any") || file is null || file.Length == 0 || file.Length > 1024L*1024*1024)
        return Results.BadRequest(new { error = "Проверьте ключ, версию, платформу и файл (до 1 ГБ)." });
    await using (var checkConnection = await db.OpenConnectionAsync(context.RequestAborted))
    await using (var duplicate = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM packages WHERE package_key=@key AND version=@version AND platform=@platform)", checkConnection))
    {
        duplicate.Parameters.AddWithValue("key", key); duplicate.Parameters.AddWithValue("version", version); duplicate.Parameters.AddWithValue("platform", platform);
        if (await duplicate.ExecuteScalarAsync(context.RequestAborted) is true) return Results.Conflict(new { error = "Такая версия уже загружена." });
    }
    var id = Guid.NewGuid();
    var destination = Path.Combine(packageRoot, id.ToString("N"));
    var stored = false;
    try
    {
    await using (var output = File.Create(destination)) await file.CopyToAsync(output, context.RequestAborted);
    string hash;
    await using (var stream = File.OpenRead(destination)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, context.RequestAborted));
    await using var connection = await db.OpenConnectionAsync(context.RequestAborted);
    await using var transaction = await connection.BeginTransactionAsync(context.RequestAborted);
    await using (var packageLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key||':'||@platform,0))",connection,transaction))
    {
        packageLock.Parameters.AddWithValue("key",key); packageLock.Parameters.AddWithValue("platform",platform);
        await packageLock.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await using (var demote = new NpgsqlCommand("UPDATE packages SET is_latest=false WHERE package_key=@key AND platform=@platform", connection, transaction))
    {
        demote.Parameters.AddWithValue("key",key); demote.Parameters.AddWithValue("platform",platform); await demote.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await using (var insert = new NpgsqlCommand("INSERT INTO packages(id,package_key,version,platform,sha256,size_bytes,download_name,storage_name,is_latest,created_at) VALUES(@id,@key,@version,@platform,@hash,@size,@name,@storage,true,now())", connection, transaction))
    {
        insert.Parameters.AddWithValue("id",id); insert.Parameters.AddWithValue("key",key); insert.Parameters.AddWithValue("version",version);
        insert.Parameters.AddWithValue("platform",platform); insert.Parameters.AddWithValue("hash",hash); insert.Parameters.AddWithValue("size",file.Length);
        insert.Parameters.AddWithValue("name",Path.GetFileName(file.FileName)); insert.Parameters.AddWithValue("storage",id.ToString("N"));
        await insert.ExecuteNonQueryAsync(context.RequestAborted);
    }
    await transaction.CommitAsync(context.RequestAborted);
    stored = true;
    return Results.Ok(new { id, key, version, platform, sha256=hash, sizeBytes=file.Length });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error="Такая версия уже загружена. Обновите список пакетов." });
    }
    finally
    {
        if (!stored && File.Exists(destination)) File.Delete(destination);
    }
});

app.Run();

static async Task<bool> SameOriginAsync(HttpContext context)
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
    return parsed.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
        && parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
}

static string LoginPage(string token, string? error = null) => $$"""
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Вход · ИТ-Сети</title><script src="/theme.js?v=1"></script><link rel="stylesheet" href="/app.css?v=7"></head>
<body class="login-screen"><main class="login-box"><h1>ИТ-Сети</h1><p>Мониторинг компьютеров</p><form method="post" action="/login"><input type="hidden" name="__RequestVerificationToken" value="{{HtmlEncoder.Default.Encode(token)}}"><label>Учётная запись<input class="field" name="username" autocomplete="username" required></label><label>Пароль<input class="field" name="password" type="password" autocomplete="current-password" required></label>{{(error is null ? "" : $"<p class='login-error'>{HtmlEncoder.Default.Encode(error)}</p>")}}<button class="primary" type="submit">Войти</button></form></main></body></html>
""";

public sealed record EnrollRequest(long CompanyId, long? SiteId, string ComputerName, string? SerialNumber, string? HardwareUuid, string? InventoryNumber);
public sealed record UpdateAssignmentRequest(long CompanyId, long? SiteId, string? InventoryNumber);
public sealed record CreateTicketRequest(Guid RequestId, string? Title, string? ServiceCode, string? Description,
    IReadOnlyList<TicketAttachmentInput>? Attachments = null);
public sealed record CreateTicketMessageRequest(Guid MessageId, string? Content,
    IReadOnlyList<TicketAttachmentInput>? Attachments = null);
