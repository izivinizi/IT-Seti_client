namespace ITSeti.Server;

public static class DeviceDisplayFilter
{
    public const string SqlPredicate = "upper(btrim(coalesce(d.computer_name, ''))) !~ '^(RT|SRV)([0-9]|[[:space:]_.-]|$)' AND upper(btrim(coalesce(d.inventory_number, ''))) !~ '^(RT|SRV)([0-9]|[[:space:]_.-]|$)'";
    public const string OkdeskEquipmentSqlPredicate = "upper(btrim(coalesce(e.inventory_number, ''))) !~ '^(RT|SRV)([0-9]|[[:space:]_.-]|$)' AND upper(btrim(coalesce(e.name, ''))) !~ '^(RT|SRV)([0-9]|[[:space:]_.-]|$)'";

    public static bool IsExcluded(string? computerName)
    {
        return IsInfrastructureIdentifier(computerName);
    }

    public static bool IsExcludedOkdeskEquipment(string? inventoryNumber, string? name) =>
        IsInfrastructureIdentifier(inventoryNumber) || IsInfrastructureIdentifier(name);

    private static bool IsInfrastructureIdentifier(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return false;
        if (string.Equals(normalized, "RT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "SRV", StringComparison.OrdinalIgnoreCase)) return true;

        var prefixLength = normalized.StartsWith("SRV", StringComparison.OrdinalIgnoreCase) ? 3 :
            normalized.StartsWith("RT", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (prefixLength == 0 || normalized.Length <= prefixLength) return false;
        var separator = normalized[prefixLength];
        return char.IsDigit(separator) || char.IsWhiteSpace(separator) || separator is '_' or '.' or '-';
    }
}
