using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ITSeti.Server;

public static class PackageVersionDetector
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)(?<version>\d+\.\d+\.\d+(?:\.\d+)?)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryDetect(string path, string fileName, out Version version)
    {
        version = new Version(0, 0, 0);
        try
        {
            if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (TryParse(fileVersion, out version)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The filename fallback keeps uploads usable on Linux and for portable EXEs.
        }

        var match = VersionPattern.Match(fileName ?? string.Empty);
        return match.Success && TryParse(match.Groups["version"].Value, out version);
    }

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = VersionPattern.Match(text.Trim());
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), parsed.Revision < 0 ? 0 : parsed.Revision);
        return true;
    }
}
