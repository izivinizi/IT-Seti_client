using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ITSeti.Server;

public sealed record ClientUpdateHint(string? ApplicationVersion, string CatalogRevision);

public sealed class ClientUpdateHints(NpgsqlDataSource db, ILogger<ClientUpdateHints> logger)
{
    public const string AuthenticatedPlatform = "ITSeti.AuthenticatedPlatform";
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<string, ClientUpdateHint>? cached;
    private DateTimeOffset expires;
    private long generation;

    public void Invalidate() => Interlocked.Increment(ref generation);

    public static string? Platform(HttpRequest request, string? osName, string? osVersion)
    {
        var explicitPlatform = request.Query["platform"].FirstOrDefault()
            ?? request.Headers["X-ITSeti-Platform"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(explicitPlatform))
            return explicitPlatform.Trim().ToLowerInvariant() is "windows" or "win7" ? explicitPlatform.Trim().ToLowerInvariant() : null;
        return (osName ?? "").Contains("Windows 7", StringComparison.OrdinalIgnoreCase) ||
            (osName ?? "").Contains("Windows7", StringComparison.OrdinalIgnoreCase) ||
            (osVersion ?? "").StartsWith("6.1", StringComparison.OrdinalIgnoreCase) ? "win7" : "windows";
    }

    public async Task<ClientUpdateHint?> GetAsync(string platform, CancellationToken cancellationToken)
    {
        if (platform is not ("windows" or "win7")) return null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var currentGeneration = Interlocked.Read(ref generation);
            if (cached is not null && DateTimeOffset.UtcNow < expires && cachedGeneration == currentGeneration)
                return cached[platform];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var connection = await db.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("""
                SELECT id,package_key,version,platform,sha256,size_bytes,download_name,product_name
                FROM packages WHERE is_latest ORDER BY package_key,platform,id
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            var packages = new List<Package>();
            while (await reader.ReadAsync(timeout.Token)) packages.Add(new(reader.GetGuid(0),reader.GetString(1),
                reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),reader.GetString(6),reader.GetString(7)));
            cached = new[] { "windows", "win7" }.ToDictionary(value => value, value => Build(packages,value));
            cachedGeneration = currentGeneration;
            expires = DateTimeOffset.UtcNow.AddSeconds(10);
            return cached[platform];
        }
        finally { gate.Release(); }
    }
    private long cachedGeneration = -1;

    private sealed record Package(Guid Id,string Key,string Version,string Platform,string Hash,long Size,string File,string Name);
    private static ClientUpdateHint Build(List<Package> packages,string platform)
    {
        var relevant = packages.Where(package => package.Platform == platform || package.Platform == "any").ToArray();
        var version = relevant.FirstOrDefault(package => package.Key == "application" && package.Platform == platform)?.Version;
        if (!Version.TryParse(version,out _)) version = null;
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(relevant)))).ToLowerInvariant();
        return new(version,revision);
    }

    public void Register(HttpContext context)
    {
        context.Response.OnStarting(async () =>
        {
            if (context.Response.StatusCode is < 200 or >= 300) return;
            if (context.Request.Path.StartsWithSegments("/api/admin/packages") &&
                context.Request.Method is "POST" or "DELETE") Invalidate();
            if (!context.Request.Path.StartsWithSegments("/api/v1") ||
                !context.Items.TryGetValue(AuthenticatedPlatform,out var value) || value is not string platform) return;
            try
            {
                var hint = await GetAsync(platform,context.RequestAborted);
                if (hint is null) return;
                if (hint.ApplicationVersion is not null) context.Response.Headers["X-ITSeti-Application-Version"] = hint.ApplicationVersion;
                context.Response.Headers["X-ITSeti-Catalog-Revision"] = hint.CatalogRevision;
            }
            catch (Exception error) when (error is NpgsqlException or OperationCanceledException or TimeoutException)
            {
                logger.LogWarning(error,"Optional client update headers unavailable");
            }
        });
    }
}
