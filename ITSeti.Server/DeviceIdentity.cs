using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ITSeti.Server;

public static class DeviceIdentity
{
    public static string? CreateFingerprint(string? serialNumber, string? hardwareUuid)
    {
        var serial = Normalize(serialNumber);
        if (IsUsable(serial)) return Hash("serial:" + serial);

        var uuid = Normalize(hardwareUuid);
        if (IsUsable(uuid)) return Hash("uuid:" + uuid);
        return null;
    }

    private static string Normalize(string? value) =>
        Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToUpperInvariant();

    private static bool IsUsable(string value) => value.Length is >= 6 and <= 128 &&
        value is not ("DEFAULTSTRING" or "TOBEFILLEDBYOEM" or "SYSTEMSERIALNUMBER" or "UNKNOWN" or "NONE") &&
        !value.All(c => c == '0') && !value.All(c => c == 'F');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
