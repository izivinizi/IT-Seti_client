using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class WindowsDiagnosticDataPolicyRunner
{
    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ITSeti", "Maintenance", "WindowsDiagnosticData", "policy-status.json");

    private sealed record PolicyResult(string Action, string State, string Message, DateTimeOffset UpdatedAt);

    public string ReadStatus() => ReadResult()?.Message ?? "Диагностические данные Windows приложением не изменялись.";

    public Task<string> MinimizeAsync() => RunAsync(MaintenanceServiceOperation.MinimizeWindowsDiagnosticData, "Minimize");
    public Task<string> RestoreAsync() => RunAsync(MaintenanceServiceOperation.RestoreWindowsDiagnosticData, "Restore");

    private static async Task<string> RunAsync(MaintenanceServiceOperation operation, string action)
    {
        var previous = ReadResult()?.UpdatedAt ?? DateTimeOffset.MinValue;
        var service = await MaintenanceServiceClient.StartAsync(operation);
        if (!service.Connected) throw new InvalidOperationException("Служба обслуживания недоступна: " + service.Message);
        if (!service.Accepted) throw new InvalidOperationException(service.Message);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ReadResult() is { } result && result.UpdatedAt > previous && result.Action == action)
            {
                if (result.State == "Failed") throw new InvalidOperationException(result.Message);
                return result.Message;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("Служба приняла запрос, но статус изменения диагностических данных не появился.");
    }

    private static PolicyResult? ReadResult()
    {
        try
        {
            return File.Exists(StatusPath)
                ? JsonSerializer.Deserialize<PolicyResult>(File.ReadAllText(StatusPath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
