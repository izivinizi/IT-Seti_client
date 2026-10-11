using System.Net;

namespace ITSeti.Server;

public sealed record IpAssignment(long CompanyId, long? SiteId);

public static class AutomaticIpEnrollment
{
    public static string? Normalize(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) ||
            IPAddress.None.Equals(address) || IPAddress.IPv6None.Equals(address)) return null;
        return address.ToString();
    }

    public static IpAssignment? ResolveUnique(IEnumerable<IpAssignment> assignments)
    {
        var distinct = assignments.Distinct().Take(2).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    public static bool CanReuseExisting(string? previousAddress, string currentAddress)
    {
        if (!IPAddress.TryParse(previousAddress, out var previous) ||
            !IPAddress.TryParse(currentAddress, out var current)) return false;
        return string.Equals(Normalize(previous), Normalize(current), StringComparison.OrdinalIgnoreCase);
    }
}
