using System.Security.Cryptography;

namespace ITSeti.Maintenance.App;

internal static class EngineerOfflineAuthentication
{
    private static readonly byte[] Salt = Convert.FromBase64String("0jKlea7n+RuKFzrtnlpCsw9PdnlEjowSdNgWjTzVo4o=");
    private static readonly byte[] ExpectedHash = Convert.FromBase64String("tZK0nPRGR21eEuuZT/epg2UyylTnUmDUgKdjp4mF9r0=");

    public static bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256) return false;
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, Salt, 300_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(candidate, ExpectedHash);
    }
}
