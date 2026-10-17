using Npgsql;

namespace ITSeti.Server;

internal static class DeviceMonitoringEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/remote-access", async (NpgsqlDataSource db, CancellationToken cancellationToken) =>
        {
            await using var connection = await db.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"""
                SELECT d.id,
                  (SELECT coalesce(nullif(r.details->>'RmsId',''),nullif(r.details->>'rmsId',''))
                   FROM check_runs r WHERE r.device_id=d.id
                     AND coalesce(nullif(r.details->>'RmsId',''),nullif(r.details->>'rmsId','')) IS NOT NULL
                   ORDER BY r.started_at DESC,r.id DESC LIMIT 1),
                  (SELECT coalesce(nullif(r.details->>'AnyDeskId',''),nullif(r.details->>'anyDeskId',''))
                   FROM check_runs r WHERE r.device_id=d.id
                     AND coalesce(nullif(r.details->>'AnyDeskId',''),nullif(r.details->>'anyDeskId','')) IS NOT NULL
                   ORDER BY r.started_at DESC,r.id DESC LIMIT 1)
                FROM devices d WHERE {DeviceDisplayFilter.SqlPredicate}
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var items = new List<object>();
            while (await reader.ReadAsync(cancellationToken)) items.Add(new {
                deviceId = reader.GetGuid(0), rms = reader.IsDBNull(1) ? null : reader.GetString(1),
                anyDesk = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
            return Results.Ok(new { items });
        });
        admin.MapGet("/devices/{id:guid}/monitoring-issues", async (Guid id, NpgsqlDataSource db, CancellationToken cancellationToken) =>
        {
            await using var connection = await db.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"""
                WITH history AS (
                    SELECT a.*,count(*) OVER(PARTITION BY a.category,a.identity) AS occurrences,
                        min(a.detected_at) OVER(PARTITION BY a.category,a.identity) AS first_at,
                        row_number() OVER(PARTITION BY a.category,a.identity ORDER BY a.detected_at DESC,a.id DESC) AS position
                    FROM monitor_alerts a JOIN devices d ON d.id=a.device_id
                    WHERE a.device_id=@device AND a.visible AND {DeviceDisplayFilter.SqlPredicate}
                )
                SELECT id,category,identity,title,detail,severity,occurrences,first_at,detected_at,run_id,state
                FROM history WHERE position=1 ORDER BY severity,detected_at DESC,id DESC
                """, connection);
            command.Parameters.AddWithValue("device", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var groups = new List<object>();
            while (await reader.ReadAsync(cancellationToken)) groups.Add(new {
                id=reader.GetInt64(0), category=reader.GetString(1), identity=reader.GetString(2),
                title=reader.GetString(3), detail=reader.GetString(4), severity=reader.GetInt16(5),
                occurrences=reader.GetInt64(6), firstDetectedAt=reader.GetDateTime(7),
                detectedAt=reader.GetDateTime(8), runId=reader.GetGuid(9), state=reader.GetString(10)
            });
            return Results.Ok(new { items=groups });
        });
        admin.MapGet("/devices/{id:guid}/monitoring-issues/history", async (Guid id, string category, string identity,
            int? page, NpgsqlDataSource db, CancellationToken cancellationToken) =>
        {
            if (category.Length > 40 || identity.Length > 1000) return Results.BadRequest();
            const int pageSize = 30;
            var offset=Math.Clamp(page ?? 0,0,100000)*pageSize;
            await using var connection=await db.OpenConnectionAsync(cancellationToken);
            await using var command=new NpgsqlCommand($"""
                SELECT a.id,a.run_id,a.detected_at,a.title,a.detail,a.severity,a.state
                FROM monitor_alerts a JOIN devices d ON d.id=a.device_id
                WHERE a.device_id=@device AND a.category=@category AND a.identity=@identity AND a.visible
                  AND {DeviceDisplayFilter.SqlPredicate}
                ORDER BY a.detected_at DESC,a.id DESC LIMIT @limit OFFSET @offset
                """,connection);
            command.Parameters.AddWithValue("device",id); command.Parameters.AddWithValue("category",category);
            command.Parameters.AddWithValue("identity",identity); command.Parameters.AddWithValue("limit",pageSize+1);
            command.Parameters.AddWithValue("offset",offset);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            var items=new List<object>();
            while(await reader.ReadAsync(cancellationToken)) items.Add(new {
                id=reader.GetInt64(0),runId=reader.GetGuid(1),detectedAt=reader.GetDateTime(2),
                title=reader.GetString(3),detail=reader.GetString(4),severity=reader.GetInt16(5),state=reader.GetString(6)
            });
            return Results.Ok(new {items=items.Take(pageSize),hasMore=items.Count>pageSize,page=offset/pageSize});
        });
    }
}
