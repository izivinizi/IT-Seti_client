namespace ITSeti.Server;

public static class OkdeskLinks
{
    public static string? IssueUrl(IConfiguration configuration, long issueId)
    {
        if (issueId <= 0) return null;
        var raw = configuration["OkdeskBaseUrl"] ?? "https://it-seti.okdesk.ru";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !baseUri.Host.EndsWith(".okdesk.ru", StringComparison.OrdinalIgnoreCase)) return null;
        return new Uri(baseUri, $"/issues/{issueId}").AbsoluteUri;
    }
}
