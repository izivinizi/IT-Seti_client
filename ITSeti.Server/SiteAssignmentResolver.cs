using Npgsql;
using NpgsqlTypes;

namespace ITSeti.Server;

public static class SiteAssignmentResolver
{
    public static async Task<IpAssignment> CompleteAsync(NpgsqlConnection connection, IpAssignment assignment,
        string? ip, Guid? excludedDevice, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        if (assignment.SiteId is not null) return assignment;
        var sites = new List<long>();
        await using (var query = new NpgsqlCommand("""
            SELECT s.id FROM service_objects s JOIN companies c ON c.id=s.company_id AND c.active
            WHERE s.company_id=@company AND s.active ORDER BY s.id LIMIT 2
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("company", assignment.CompanyId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) sites.Add(reader.GetInt64(0));
        }
        if (sites.Count == 1) return assignment with { SiteId = sites[0] };
        if (sites.Count == 0 || ip is null) return assignment;
        sites.Clear();
        await using (var query = new NpgsqlCommand("""
            SELECT DISTINCT d.service_object_id FROM devices d
            JOIN service_objects s ON s.id=d.service_object_id AND s.company_id=d.company_id AND s.active
            JOIN companies c ON c.id=d.company_id AND c.active
            WHERE d.company_id=@company AND (@excluded IS NULL OR d.id<>@excluded)
              AND d.last_ip=@ip::inet AND d.last_seen_at>=now()-interval '120 days'
            LIMIT 2
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("company", assignment.CompanyId);
            query.Parameters.Add("excluded", NpgsqlDbType.Uuid).Value = (object?)excludedDevice ?? DBNull.Value;
            query.Parameters.AddWithValue("ip", ip);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) sites.Add(reader.GetInt64(0));
        }
        return sites.Count == 1 ? assignment with { SiteId = sites[0] } : assignment;
    }
}
