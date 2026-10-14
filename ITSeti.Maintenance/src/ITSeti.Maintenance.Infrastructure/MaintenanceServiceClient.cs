using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record MaintenanceServiceCallResult(bool Connected, bool Accepted, string Message, string? Version = null)
{
    public static MaintenanceServiceCallResult Unavailable(string message) => new(false, false, message);
}

public static class MaintenanceServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string? authorizationToken;

    public static Task<MaintenanceServiceCallResult> AuthenticateEngineerAsync(string password,
        CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("authenticate", Password: password), cancellationToken);

    public static void ClearEngineerAuthorization() => authorizationToken = null;

    public static Task<MaintenanceServiceCallResult> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("ping"), cancellationToken);

    public static Task<MaintenanceServiceCallResult> RefreshTicketUpdatesAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("refresh-ticket-updates"), cancellationToken);

    public static Task<MaintenanceServiceCallResult> SaveEnrollmentAsync(DeviceEnrollmentData enrollment,
        CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("save-enrollment", Enrollment: enrollment), cancellationToken);

    public static Task<MaintenanceServiceCallResult> StartAsync(
        MaintenanceServiceOperation operation, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("start", operation), cancellationToken);

    public static Task<MaintenanceServiceCallResult> StopAsync(
        MaintenanceServiceOperation operation, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("stop", operation), cancellationToken);

    public static Task<MaintenanceServiceCallResult> LaunchSystemToolAsync(
        string key, int sessionId, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("launch", MaintenanceServiceOperation.OpenSystemTool,
            key, SessionId: sessionId), cancellationToken);

    public static Task<MaintenanceServiceCallResult> LaunchUtilityAsync(
        string key, string? argument, int sessionId, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("launch", MaintenanceServiceOperation.OpenUtility,
            key, argument, sessionId), cancellationToken);

    public static Task<MaintenanceServiceCallResult> LaunchServerPackageAsync(
        Guid packageId, string requestId, int sessionId, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("install-package", MaintenanceServiceOperation.OpenServerPackage,
            packageId.ToString("D"), requestId, sessionId), cancellationToken);

    private static async Task<MaintenanceServiceCallResult> SendAsync(
        MaintenanceServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(request.Command, "authenticate", StringComparison.OrdinalIgnoreCase))
                request = request with { AuthorizationToken = authorizationToken };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var pipe = new NamedPipeClientStream(".", MaintenanceServiceProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);
            var payload = JsonSerializer.Serialize(request, JsonOptions);
            if (payload.Length > MaintenanceServiceProtocol.MaximumMessageCharacters)
                return new(true, false, "Запрос к службе слишком большой.");
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) return new(true, false, "Служба не вернула ответ.");
            var response = JsonSerializer.Deserialize<MaintenanceServiceResponse>(line, JsonOptions);
            if (response?.Success == true && string.Equals(request.Command, "authenticate", StringComparison.OrdinalIgnoreCase))
                authorizationToken = response.AuthorizationToken;
            return response is null
                ? new(true, false, "Служба вернула некорректный ответ.")
                : new(true, response.Success, response.Message, response.Version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MaintenanceServiceCallResult.Unavailable("Служба обслуживания не ответила.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException)
        {
            return MaintenanceServiceCallResult.Unavailable(ex.Message);
        }
    }
}
