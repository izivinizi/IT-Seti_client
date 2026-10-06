using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ITSeti.Server;

public static class RegistrationPassword
{
    public const string HeaderName = "X-Registration-Password";

    public static bool Verify(HttpRequest request, IConfiguration configuration)
    {
        var password = request.Headers[HeaderName].ToString();
        var stored = configuration["RegistrationPasswordHash"]?.Split(':');
        if (password.Length is < 8 or > 128 || stored is not { Length: 3 } ||
            !int.TryParse(stored[0], out var iterations) || iterations is < 100_000 or > 1_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(stored[1]);
            var expected = Convert.FromBase64String(stored[2]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}

public static class DeviceKeys
{
    public const string IdHeader = "X-Device-Id";
    public const string KeyHeader = "X-Device-Key";
    public const string SupportKeyHeader = "X-Support-Key";

    public static Task<bool> AuthenticateAsync(HttpRequest request, NpgsqlDataSource dataSource) =>
        AuthenticateKeyAsync(request, dataSource, KeyHeader, "api_key_hash");

    public static Task<bool> AuthenticateSupportAsync(HttpRequest request, NpgsqlDataSource dataSource) =>
        AuthenticateKeyAsync(request, dataSource, SupportKeyHeader, "support_key_hash");

    private static async Task<bool> AuthenticateKeyAsync(HttpRequest request, NpgsqlDataSource dataSource,
        string header, string column)
    {
        if (!Guid.TryParse(request.Headers[IdHeader].ToString(), out var deviceId)) return false;
        var key = request.Headers[header].ToString();
        if (key.Length is < 32 or > 100) return false;
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {column} FROM devices WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", deviceId);
        var stored = await command.ExecuteScalarAsync() as string;
        if (stored is null) return false;
        var expected = Encoding.ASCII.GetBytes(stored);
        var actual = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public static class JsonRead
{
    public static JsonElement? Property(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return property.Value;
        return null;
    }

    public static string? String(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString();
    }

    public static Guid? Guid(JsonElement root, params string[] names) => System.Guid.TryParse(String(root, names), out var id) ? id : null;

    public static DateTimeOffset? Date(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        return value.HasValue && value.Value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.Value.GetString(), out var date) ? date.ToUniversalTime() : null;
    }

    public static double? Number(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.Value.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }

    public static bool? Bool(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Value.GetBoolean();
        return bool.TryParse(value.Value.ToString(), out var result) ? result : null;
    }

    public static IReadOnlyList<string> StringList(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null || value.Value.ValueKind != JsonValueKind.Array) return [];
        return value.Value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Take(200).ToArray();
    }
}
