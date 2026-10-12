namespace ITSeti.Server;

public static class InventoryNumbers
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length == 0) return null;
        if (normalized.All(char.IsDigit)) normalized = normalized.TrimStart('0');
        return normalized.Length == 0 ? null : normalized;
    }
}
