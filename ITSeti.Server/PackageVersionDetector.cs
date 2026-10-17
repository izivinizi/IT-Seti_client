using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ITSeti.Server;

public static class PackageVersionDetector
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)(?<version>\d+\.\d+\.\d+(?:\.\d+)?)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record PackageMetadata(string Name, Version? Version);

    public static PackageMetadata Detect(string path, string fileName)
    {
        var safeFileName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var name = DetectName(null, null, safeFileName);
        Version? version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = DetectName(info.ProductName, info.FileDescription, safeFileName);
            if (TryParse(info.ProductVersion, out var productVersion)) version = productVersion;
            else if (TryParse(info.FileVersion, out var fileVersion)) version = fileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            // Portable installers can omit PE version resources; use their filenames instead.
        }

        try
        {
            var resource = PeVersionResource.Read(path);
            if (resource is not null)
            {
                if (!string.IsNullOrWhiteSpace(resource.ProductName) || !string.IsNullOrWhiteSpace(resource.Description))
                    name = DetectName(resource.ProductName, resource.Description, safeFileName);
                if (TryParse(resource.ProductVersion, out var productVersion)) version = productVersion;
                else if (TryParse(resource.FileVersion, out var fileVersion)) version = fileVersion;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
            or ArgumentException or OverflowException or InvalidOperationException or NotSupportedException)
        {
            // Non-PE, absent, truncated or malformed resources retain the platform and filename fallbacks.
        }

        if (version is null)
        {
            var match = VersionPattern.Match(safeFileName);
            if (match.Success && TryParse(match.Groups["version"].Value, out var filenameVersion))
                version = filenameVersion;
        }

        return new(TrimName(name), version);
    }

    public static bool TryDetect(string path, string fileName, out Version version)
    {
        var metadata = Detect(path, fileName);
        version = metadata.Version ?? new Version(0, 0, 0);
        return metadata.Version is not null;
    }

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = VersionPattern.Match(text.Trim());
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed)) return false;
        version = parsed.Revision < 0 ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0))
            : new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), parsed.Revision);
        return true;
    }

    public static string DetectName(string? productName, string? fileDescription, string fileName)
    {
        var safeFileName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var fallbackName = Path.GetFileNameWithoutExtension(safeFileName);
        fallbackName = Regex.Replace(VersionPattern.Replace(fallbackName, " "), @"[\s._-]+", " ").Trim();
        return TrimName(FirstNonEmpty(productName, fileDescription, fallbackName, "Программное обеспечение"));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string TrimName(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160].TrimEnd();
    }
}
