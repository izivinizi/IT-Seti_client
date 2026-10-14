using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text;
using System.Text.Json;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

internal sealed record ServerCatalogCompany(long Id, string Name);
internal sealed record ServerCatalogSite(long Id, string Name, string? Address, long CompanyId)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Address) ? Name : $"{Name} · {Address}";
}
internal sealed record ServerAssignment(long CompanyId, long? SiteId, string Company, string? Site);

internal sealed class ServerEnrollmentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private const string ServerUrl = "https://it-seti.nylenz.ru/";
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
    private static readonly string DevicePath = Path.Combine(Root, "server-device.json");
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private DeviceRecord? device;

    public async Task<bool> TryAutomaticEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        device = ReadDevice();
        if (device is not null)
        {
            using var resolve = await SendAsync(HttpMethod.Post, "api/v1/assignment/resolve",
                new { inventoryNumber = ReadInventory() }, cancellationToken);
            var result = await resolve.Content.ReadFromJsonAsync<ResolveAssignmentResponse>(JsonOptions, cancellationToken);
            return result?.Matched == true;
        }

        using var response = await SendEnrollmentAsync("api/v1/auto-enroll", BuildEnrollmentBody(null, null), cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadErrorAsync(response));
        await SaveEnrollmentResponseAsync(response, cancellationToken);
        return true;
    }

    public async Task<(IReadOnlyList<ServerCatalogCompany> Companies, IReadOnlyList<ServerCatalogSite> Sites, ServerAssignment Assignment)> LoadAsync(CancellationToken cancellationToken = default)
    {
        device = ReadDevice();
        using var catalog = device is null
            ? await http.GetAsync(new Uri(new Uri(ServerUrl), "api/v1/catalog"), cancellationToken)
            : await SendAsync(HttpMethod.Get, "api/v1/catalog", null, cancellationToken);
        if (!catalog.IsSuccessStatusCode) throw new InvalidOperationException(await ReadErrorAsync(catalog));
        var catalogBody = await catalog.Content.ReadFromJsonAsync<CatalogResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Сервер вернул пустой каталог компаний.");
        if (device is null)
            return (catalogBody.Companies ?? [], catalogBody.Sites ?? [], new ServerAssignment(0, null, "", null));

        using var assignmentResponse = await SendAsync(HttpMethod.Get, "api/v1/assignment", null, cancellationToken);
        var assignment = await assignmentResponse.Content.ReadFromJsonAsync<ServerAssignment>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Сервер не вернул текущую привязку ПК.");
        return (catalogBody.Companies ?? [], catalogBody.Sites ?? [], assignment);
    }

    public async Task SaveAssignmentAsync(long companyId, long? siteId, string? inventoryNumber, CancellationToken cancellationToken = default)
    {
        if (device is null)
        {
            using var enrollment = await SendEnrollmentAsync("api/v1/enroll", BuildEnrollmentBody(companyId, siteId, inventoryNumber), cancellationToken);
            if (!enrollment.IsSuccessStatusCode) throw new InvalidOperationException(await ReadErrorAsync(enrollment));
            await SaveEnrollmentResponseAsync(enrollment, cancellationToken);
            return;
        }
        using var response = await SendAsync(HttpMethod.Put, "api/v1/assignment",
            new { companyId, siteId, inventoryNumber = inventoryNumber ?? ReadInventory() }, cancellationToken);
        await SaveDeviceAsync(device with { CompanyId = companyId, SiteId = siteId }, cancellationToken);
    }

    public async Task AllowProcessAsync(string name, string? publisher, string signature, CancellationToken cancellationToken = default)
    {
        device = ReadDevice();
        if (device is null) throw new InvalidOperationException("Компьютер ещё не подключён к серверу.");
        using var response = await SendAsync(HttpMethod.Post, "api/v1/process-policy/allow",
            new { name, publisher, signature }, cancellationToken);
        await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task TrustPublisherAsync(string publisher, string signature, CancellationToken cancellationToken = default)
    {
        device = ReadDevice();
        if (device is null) throw new InvalidOperationException("Компьютер ещё не подключён к серверу.");
        if (!string.Equals(signature, "Valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Издатель не подтверждён действительной цифровой подписью.");
        using var response = await SendAsync(HttpMethod.Post, "api/v1/process-policy/allow-publisher",
            new { publisher, signature }, cancellationToken);
        await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static object BuildEnrollmentBody(long? companyId, long? siteId, string? inventoryNumber = null)
    {
        return new
        {
            ComputerName = Environment.MachineName,
            SerialNumber = ReadWmi("Win32_BIOS", "SerialNumber"),
            HardwareUuid = ReadWmi("Win32_ComputerSystemProduct", "UUID"),
            InventoryNumber = inventoryNumber ?? ReadInventory(),
            CompanyId = companyId ?? 0,
            SiteId = siteId
        };
    }

    private async Task<HttpResponseMessage> SendEnrollmentAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ServerUrl), path))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        return await http.SendAsync(request, cancellationToken);
    }

    private async Task SaveEnrollmentResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Сервер не вернул ключ подключения.");
        if (!Guid.TryParse(result.DeviceId, out _) || string.IsNullOrWhiteSpace(result.DeviceKey))
            throw new InvalidDataException("Сервер вернул неполные данные подключения.");
        device = new DeviceRecord(ServerUrl, result.DeviceId, result.DeviceKey, result.SupportKey, result.CompanyId, result.SiteId);
        await SaveDeviceAsync(device, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (device is null) throw new InvalidOperationException("Данные подключения к серверу не найдены.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(device.ServerUrl), path));
        request.Headers.Add("X-Device-Id", device.DeviceId);
        request.Headers.Add("X-Device-Key", device.DeviceKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response);
            response.Dispose();
            throw new InvalidOperationException(error);
        }
        return response;
    }

    private static DeviceRecord? ReadDevice()
    {
        try
        {
            if (!File.Exists(DevicePath)) return null;
            var record = JsonSerializer.Deserialize<DeviceRecord>(File.ReadAllText(DevicePath), JsonOptions);
            return record is not null && Uri.TryCreate(record.ServerUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(record.DeviceId, out _) && !string.IsNullOrWhiteSpace(record.DeviceKey) && record.DeviceKey.Length >= 16 ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static async Task SaveDeviceAsync(DeviceRecord value, CancellationToken cancellationToken)
    {
        var result = await MaintenanceServiceClient.SaveEnrollmentAsync(new DeviceEnrollmentData(
            value.ServerUrl, value.DeviceId, value.DeviceKey, value.SupportKey,
            value.CompanyId, value.SiteId), cancellationToken);
        if (!result.Accepted)
            throw new IOException("Не удалось сохранить привязку ПК через службу: " + result.Message);
    }

    private static string? ReadInventory()
    {
        try
        {
            var path = Path.Combine(Root, "inventory.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    private static string? ReadWmi(string className, string propertyName)
    {
        try
        {
            using var search = new ManagementObjectSearcher("root\\cimv2", $"SELECT {propertyName} FROM {className}");
            using var values = search.Get();
            var value = values.Cast<ManagementObject>().Select(item => Convert.ToString(item[propertyName])).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            return value?.Trim();
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch (JsonException) { }
        return $"Сервер вернул HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }

    public void Dispose() => http.Dispose();

    private sealed record DeviceRecord(string ServerUrl, string DeviceId, string DeviceKey, string? SupportKey, long? CompanyId, long? SiteId);
    private sealed record EnrollmentResponse(string DeviceId, string DeviceKey, string SupportKey, long? CompanyId, long? SiteId);
    private sealed record CatalogResponse(List<ServerCatalogCompany>? Companies, List<ServerCatalogSite>? Sites);
    private sealed record ResolveAssignmentResponse(bool Matched, string? MatchedBy, long? CompanyId, long? SiteId);
}
