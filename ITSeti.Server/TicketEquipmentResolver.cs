using Npgsql;

namespace ITSeti.Server;

internal static class TicketEquipmentResolver
{
    public static async Task<long[]> ResolveAsync(NpgsqlConnection connection, Guid deviceId, long companyId,
        CancellationToken cancellationToken)
    {
        var inventory = InventoryNumbers.SqlExpression("upper(regexp_replace(coalesce(d.inventory_number,''), '[^[:alnum:]]', '', 'g'))");
        await using var command = new NpgsqlCommand($"""
            SELECT e.id FROM okdesk_equipment e JOIN devices d ON d.id=@device
            WHERE e.company_id=@company AND e.normalized_inventory_number={inventory}
            ORDER BY e.id LIMIT 2
            """, connection);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("company", companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        // Duplicate inventory numbers are ambiguous; never attach another asset
        // just because it was first in the catalogue.
        return ids.Count == 1 ? ids.ToArray() : [];
    }
}
