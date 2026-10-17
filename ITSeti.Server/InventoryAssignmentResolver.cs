using Npgsql;

namespace ITSeti.Server;

public static class InventoryAssignmentResolver
{
    public static async Task<IpAssignment?> ResolveAsync(NpgsqlConnection connection, string inventory,
        CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        var deviceInventory = InventoryNumbers.SqlExpression("upper(regexp_replace(coalesce(d.inventory_number,''), '[^[:alnum:]]', '', 'g'))");
        await using var command = new NpgsqlCommand($"""
            SELECT DISTINCT source.company_id,source.service_object_id
            FROM (
                SELECT d.company_id,d.service_object_id
                FROM devices d
                WHERE {deviceInventory}=@inventory
                UNION
                SELECT e.company_id,e.service_object_id
                FROM okdesk_equipment e WHERE e.normalized_inventory_number=@inventory
            ) source
            JOIN companies c ON c.id=source.company_id AND c.active
            LEFT JOIN service_objects s ON s.id=source.service_object_id AND s.company_id=source.company_id AND s.active
            WHERE source.service_object_id IS NULL OR s.id IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("inventory", inventory);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<IpAssignment>();
        while (await reader.ReadAsync(cancellationToken))
            candidates.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        var companies = candidates.Select(candidate => candidate.CompanyId).Distinct().Take(2).ToArray();
        if (companies.Length != 1) return null;
        var sites = candidates.Where(candidate => candidate.SiteId is not null)
            .Select(candidate => candidate.SiteId).Distinct().Take(2).ToArray();
        return sites.Length > 1 ? null : new IpAssignment(companies[0], sites.FirstOrDefault());
    }
}
