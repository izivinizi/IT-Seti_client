using System.Net.Http.Headers;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ApplicationRelease(Version Version, string Tag, string Digest, long Size, string DownloadUrl);

public sealed class ServerApplicationUpdateClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient client;
    private readonly string serverBase;
    private readonly string deviceId;
    private readonly string deviceKey;
    private readonly string platform;

    public ServerApplicationUpdateClient()
    {
        var devicePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSeti", "Maintenance", "server-device.json");
        if (!TryReadDevice(devicePath, out var configuredServer, out var configuredId, out var configuredKey))
            throw new InvalidOperationException("Компьютер ещё не подключён к серверу.");
        serverBase = configuredServer;
        deviceId = configuredId;
        deviceKey = configuredKey;
        platform = Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Minor >= 1
            && Environment.OSVersion.Version.Build < 9200 ? "win7" : "windows";
        client = CreateClient();
    }

    internal ServerApplicationUpdateClient(HttpClient client, string serverBase, string deviceId, string deviceKey,
        string platform = "windows")
    {
        this.client = client;
        this.serverBase = NormalizeBase(serverBase);
        this.deviceId = deviceId;
        this.deviceKey = deviceKey;
        this.platform = platform;
    }

    public async Task<ApplicationRelease?> GetUpdateAsync(Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(serverBase), "api/v1/application-update?platform=" + Uri.EscapeDataString(platform)));
        request.Headers.Add("X-Device-Id", deviceId);
        request.Headers.Add("X-Device-Key", deviceKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var package = await JsonSerializer.DeserializeAsync<ServerPackage>(stream, JsonOptions, cancellationToken);
        if (package is null) throw new InvalidDataException("Сервер вернул пустые данные обновления.");
        if (!Guid.TryParse(package.Id, out var id) || id == Guid.Empty ||
            !string.Equals(package.Key, "application", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.Platform, platform, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(package.Version, out var version) || version.Build < 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(package.Sha256 ?? "", "^[0-9a-fA-F]{64}$") ||
            package.SizeBytes is < 1 or > 500_000_000 ||
            !string.Equals(Path.GetExtension(package.FileName), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер вернул некорректные параметры установщика.");

        var normalizedRelease = Normalize(version);
        var normalizedCurrent = Normalize(currentVersion);
        return normalizedRelease > normalizedCurrent
            ? new ApplicationRelease(version, "server", "sha256:" + package.Sha256,
                package.SizeBytes, new Uri(new Uri(serverBase), "api/v1/packages/" + id.ToString("D") + "/download").ToString())
            : null;
    }

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("ITSeti-Maintenance/2.3.7");
        result.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return result;
    }

    private static bool TryReadDevice(string path, out string server, out string id, out string key)
    {
        server = id = key = "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            server = NormalizeBase(root.GetProperty("serverUrl").GetString() ?? "");
            id = root.GetProperty("deviceId").GetString() ?? "";
            key = root.GetProperty("deviceKey").GetString() ?? "";
            return Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && Guid.TryParse(id, out _) && key.Length >= 16;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        { return false; }
    }

    private static string NormalizeBase(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(value.Build, 0));

    private sealed record ServerPackage(string Id, string Key, string Version, string Platform, string Sha256,
        long SizeBytes, string FileName);
}
