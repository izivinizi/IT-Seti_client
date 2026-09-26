using System.Net.Http.Headers;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ApplicationRelease(Version Version, string Tag, string Digest, long Size, string DownloadUrl);

public sealed class GitHubReleaseClient
{
    private const string Repository = "izivinizi/IT-Seti_client";
    private const string ManifestUrl = "https://raw.githubusercontent.com/izivinizi/IT-Seti_client/main/release.json";
    private const string InstallerName = "ITSeti-Maintenance-Setup.exe";
    private readonly HttpClient client;
    private readonly string manifestUrl;

    public GitHubReleaseClient() : this(CreateClient(), ManifestUrl) { }

    internal GitHubReleaseClient(HttpClient client, string manifestUrl)
    {
        this.client = client;
        this.manifestUrl = manifestUrl;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ITSeti-Maintenance/1.0.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<ApplicationRelease?> GetUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var versionText = root.GetProperty("version").GetString() ?? "";
        var tag = root.GetProperty("tag").GetString() ?? "";
        var assetName = root.GetProperty("assetName").GetString() ?? "";
        var digest = root.GetProperty("digest").GetString() ?? "";
        var downloadUrl = root.GetProperty("downloadUrl").GetString() ?? "";
        var size = root.GetProperty("size").GetInt64();

        if (!System.Text.RegularExpressions.Regex.IsMatch(versionText, @"^\d+\.\d+\.\d+(?:\.\d+)?$") ||
            !Version.TryParse(versionText, out var version) || tag != $"v{versionText}")
            throw new InvalidDataException("GitHub update manifest contains an invalid version or tag.");
        if (!string.Equals(assetName, InstallerName, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub update manifest does not name the expected installer.");
        if (size is < 1 or > 500_000_000 ||
            !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("GitHub update manifest has an invalid size or SHA-256 digest.");

        var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{InstallerName}";
        if (!string.Equals(downloadUrl, expectedUrl, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub update manifest points outside the trusted release asset.");

        var normalizedRelease = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        var normalizedCurrent = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0));
        return normalizedRelease > normalizedCurrent
            ? new ApplicationRelease(version, tag, digest, size, downloadUrl)
            : null;
    }
}
