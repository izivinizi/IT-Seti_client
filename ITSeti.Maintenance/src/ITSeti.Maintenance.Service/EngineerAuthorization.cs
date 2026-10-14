using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ITSeti.Maintenance.Service;

internal sealed class EngineerAuthorization
{
    private sealed record Session(int ProcessId, int WindowsSessionId, DateTimeOffset ExpiresAt);
    private static readonly byte[] Salt = Convert.FromBase64String("0jKlea7n+RuKFzrtnlpCsw9PdnlEjowSdNgWjTzVo4o=");
    private static readonly byte[] ExpectedHash = Convert.FromBase64String("tZK0nPRGR21eEuuZT/epg2UyylTnUmDUgKdjp4mF9r0=");

    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, (int Failures, DateTimeOffset RetryAt)> failures = new();

    public string? Authenticate(string? password, ServiceClientIdentity client)
    {
        if (!client.IsInstalledClient || client.ProcessId <= 0 || client.SessionId <= 0 ||
            string.IsNullOrEmpty(password) || password.Length > 256) return null;
        if (failures.TryGetValue(client.ProcessId, out var failed) && failed.RetryAt > DateTimeOffset.UtcNow)
            return null;
        if (!Verify(password))
        {
            failures.AddOrUpdate(client.ProcessId, _ => (1, DateTimeOffset.MinValue),
                (_, old) => (old.Failures + 1, old.Failures >= 4 ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MinValue));
            return null;
        }
        failures.TryRemove(client.ProcessId, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[token] = new(client.ProcessId, client.SessionId, DateTimeOffset.UtcNow.AddMinutes(30));
        return token;
    }

    public bool IsAuthorized(string? token, ServiceClientIdentity client)
    {
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var session)) return false;
        if (session.ProcessId != client.ProcessId || session.WindowsSessionId != client.SessionId ||
            session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    private bool Verify(string password)
    {
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, Salt, 300_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(candidate, ExpectedHash);
    }
}
