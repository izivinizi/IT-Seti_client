using System.Text.Json;
using System.Diagnostics;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ServerSoftwarePackage(Guid Id, string Key, string Version, string Platform, string Sha256, long SizeBytes, string FileName);

public static class ServerSoftwareCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ITSeti","Maintenance","software-catalog.json");
    public static bool IsConnected => File.Exists(Path.Combine(Path.GetDirectoryName(CachePath)!, "server-device.json"));
    public static async Task RefreshAsync()
    {
        if (!IsConnected) return;
        var before = File.GetLastWriteTimeUtc(CachePath);
        var service = await MaintenanceServiceClient.StartAsync(MaintenanceServiceOperation.RefreshSoftwareCatalog);
        if (service.Connected)
        {
            if (!service.Accepted) throw new IOException(service.Message);
        }
        else
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "/Run", "/TN", "ITSeti-Maintenance-Catalog" }
            }) ?? throw new IOException("Не удалось запустить получение каталога ПО.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Задача получения ПО недоступна. Установите обновлённую версию приложения.");
        }
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (File.GetLastWriteTimeUtc(CachePath) != before) return;
        }
        throw new IOException("Список ПО не обновлён. Проверьте подключение к серверу; сохранённый список остаётся доступен.");
    }
    public static IReadOnlyList<ServerSoftwarePackage> Read(string? cacheFile = null)
    {
        var path = cacheFile ?? CachePath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return [];
            return (JsonSerializer.Deserialize<ServerSoftwarePackage[]>(File.ReadAllText(path),Options) ?? [])
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Key) && x.Platform is "windows" or "any" && !x.Key.Equals("application",StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Key,StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(x => x.Platform == "windows" ? 0 : 1).First()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
