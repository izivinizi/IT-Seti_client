using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record MaintenanceServiceCallResult(bool Connected, bool Accepted, string Message)
{
    public static MaintenanceServiceCallResult Unavailable(string message) => new(false, false, message);
}

public static class MaintenanceServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task<MaintenanceServiceCallResult> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("ping"), cancellationToken);

    public static Task<MaintenanceServiceCallResult> StartAsync(
        MaintenanceServiceOperation operation, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("start", operation), cancellationToken);

    public static Task<MaintenanceServiceCallResult> StopAsync(
        MaintenanceServiceOperation operation, CancellationToken cancellationToken = default) =>
        SendAsync(new MaintenanceServiceRequest("stop", operation), cancellationToken);

    private static async Task<MaintenanceServiceCallResult> SendAsync(
        MaintenanceServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
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
            return response is null
                ? new(true, false, "Служба вернула некорректный ответ.")
                : new(true, response.Success, response.Message);
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
