using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ServerSoftwarePackage(Guid Id, string Key, string Version, string Platform, string Sha256, long SizeBytes, string FileName);

public static class ServerSoftwareCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<ServerSoftwarePackage> Read()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ITSeti","Maintenance","software-catalog.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return [];
            return (JsonSerializer.Deserialize<ServerSoftwarePackage[]>(File.ReadAllText(path),Options) ?? [])
                .Where(x => x is not null && x.Platform is "windows" or "any" && x.Key != "application")
                .GroupBy(x => x.Key,StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(x => x.Platform == "windows" ? 0 : 1).First()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
