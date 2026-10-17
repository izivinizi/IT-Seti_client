namespace ITSeti.Maintenance.Infrastructure;

public static class ServerUpdateHint
{
    private static readonly object Gate = new();
    private static Version? pending;
    private static Version? consumed;

    public static void Observe(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (!response.IsSuccessStatusCode || uri?.Scheme != "https" ||
            !uri.Host.Equals("it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) ||
            !response.Headers.TryGetValues("X-ITSeti-Application-Version", out var values)) return;
        var headers = values.Take(2).ToArray();
        if (headers.Length != 1) return;
        var header = headers[0];
        if (header is null || header.Length > 32 || !Version.TryParse(header, out var version) || version.Build < 0) return;
        lock (Gate)
            if ((consumed is null || version > consumed) && (pending is null || version > pending)) pending = version;
    }

    public static bool TakeNewerThan(Version current)
    {
        lock (Gate)
        {
            if (pending is null) return false;
            var version = pending;
            pending = null;
            consumed = version;
            return version > current;
        }
    }
}
