using System.Net.Http.Headers;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ApplicationRelease(Version Version, string Tag, string Digest, long Size);

public sealed class GitHubReleaseClient
{
    private const string ReleaseApi = "https://api.github.com/repos/izivinizi/IT-Seti_client/releases/latest";
    private const string InstallerName = "ITSeti-Maintenance-Setup.exe";
    private readonly HttpClient client;
    private readonly string releaseApi;

    public GitHubReleaseClient() : this(CreateClient(), ReleaseApi) { }

    internal GitHubReleaseClient(HttpClient client, string releaseApi)
    {
        this.client = client;
        this.releaseApi = releaseApi;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ITSeti-Maintenance/0.9.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<ApplicationRelease?> GetUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(releaseApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var versionText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version)) throw new InvalidDataException("GitHub release has an invalid version tag.");
        var normalizedRelease = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        var normalizedCurrent = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0));
        if (normalizedRelease <= normalizedCurrent) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), InstallerName, StringComparison.Ordinal));
        if (asset.ValueKind is JsonValueKind.Undefined) throw new InvalidDataException("New GitHub release does not contain the application installer.");

        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var digestProperty) ? digestProperty.GetString() : null;
        var state = asset.GetProperty("state").GetString();
        if (!string.Equals(state, "uploaded", StringComparison.Ordinal) || size is < 1 or > 500_000_000 ||
            digest is null || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("New GitHub release installer has no valid size or SHA-256 digest.");

        return new ApplicationRelease(version, tag, digest, size);
    }
}
