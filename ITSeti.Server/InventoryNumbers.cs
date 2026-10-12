namespace ITSeti.Server;

public static class InventoryNumbers
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length == 0) return null;
        if (normalized.StartsWith("PC", StringComparison.Ordinal) && normalized.Length >= 7 &&
            normalized.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
            normalized = normalized[^4..];
        if (normalized.All(char.IsDigit)) normalized = normalized.TrimStart('0');
        return normalized.Length == 0 ? null : normalized;
    }

    public static string SqlExpression(string input) =>
        $"CASE WHEN {input} ~ '^PC[0-9]{{5,}}$' THEN nullif(ltrim(right({input},4),'0'),'') " +
        $"ELSE CASE WHEN {input} ~ '^[0-9]+$' THEN nullif(ltrim({input},'0'),'') ELSE {input} END END";
}
