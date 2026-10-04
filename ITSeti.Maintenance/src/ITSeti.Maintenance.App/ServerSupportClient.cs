using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace ITSeti.Maintenance.App;

internal sealed class ServerSupportClient : IDisposable
{
    private readonly HttpClient client;

    private ServerSupportClient(HttpClient client) => this.client = client;

    public static ServerSupportClient? Open()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSeti", "Maintenance", "server-support.json");
        if (!File.Exists(path)) return null;
        using var file = JsonDocument.Parse(File.ReadAllText(path));
        var root = file.RootElement;
        var uri = new Uri(root.GetProperty("serverUrl").GetString()!);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Адрес сервера заявок недействителен.");
        var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-Device-Id", root.GetProperty("deviceId").GetString());
        http.DefaultRequestHeaders.Add("X-Support-Key", root.GetProperty("supportKey").GetString());
        return new ServerSupportClient(http);
    }

    public async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        await EnsureSuccessAsync(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public async Task<JsonDocument> PostAsync(string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        await EnsureSuccessAsync(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public async Task<byte[]> DownloadAsync(string path)
    {
        using var response = await client.GetAsync(path);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string? error = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (body.RootElement.TryGetProperty("error", out var value)) error = value.GetString();
        }
        catch (JsonException) { }
        throw new InvalidOperationException(error ?? $"Сервер заявок ответил HTTP {(int)response.StatusCode}.");
    }

    public void Dispose() => client.Dispose();
}
