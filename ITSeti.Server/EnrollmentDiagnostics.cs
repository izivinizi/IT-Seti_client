using System.Text.Json;

namespace ITSeti.Server;

public sealed record EnrollmentDiagnostic(DateTimeOffset At, string RequestId, string Path, int Status,
    string Reason, long? CompanyId, long? SiteId, bool? HasSerial, bool? HasUuid);

public sealed class EnrollmentDiagnostics(IConfiguration configuration, ILogger<EnrollmentDiagnostics> logger)
{
    private readonly object gate = new();
    private readonly string file = Path.Combine(configuration["DataRoot"] ?? "/data", "enrollment-diagnostics.jsonl");

    public IResult Reject(HttpContext context, int status, string reason, string message, EnrollRequest? body = null)
    {
        Record(context, status, reason, body);
        return Results.Json(new { error = message, requestId = context.TraceIdentifier }, statusCode: status);
    }

    public void Record(HttpContext context, int status, string reason, EnrollRequest? body = null)
    {
        context.Items[nameof(EnrollmentDiagnostics)] = true;
        var entry = new EnrollmentDiagnostic(DateTimeOffset.UtcNow, context.TraceIdentifier,
            context.Request.Path.Value ?? "", status, reason, body?.CompanyId, body?.SiteId,
            body is null ? null : !string.IsNullOrWhiteSpace(body.SerialNumber),
            body is null ? null : !string.IsNullOrWhiteSpace(body.HardwareUuid));
        logger.LogInformation("Enrollment {RequestId}: {Status} {Reason}", entry.RequestId, status, reason);
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Move(file, file + ".previous", overwrite: true);
                File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (IOException) { logger.LogWarning("Enrollment diagnostic file could not be written"); }
        catch (UnauthorizedAccessException) { logger.LogWarning("Enrollment diagnostic file is not writable"); }
    }

    public IReadOnlyList<EnrollmentDiagnostic> Read()
    {
        lock (gate)
        {
            if (!File.Exists(file)) return [];
            return File.ReadLines(file).TakeLast(200).Select(line =>
            {
                try { return JsonSerializer.Deserialize<EnrollmentDiagnostic>(line); }
                catch (JsonException) { return null; }
            }).OfType<EnrollmentDiagnostic>().Reverse().ToArray();
        }
    }
}
