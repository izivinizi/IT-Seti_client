using System.Text.Json;
using Npgsql;

namespace ITSeti.Server;

public static class MonitoringHistory
{
    public static async Task ReconcileAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        // Reclassify existing alerts without deleting reports or their audit history.
        var runs = new List<(Guid Id, Guid Device, JsonElement Report, JsonElement? Previous, JsonElement? Events)>();
        await using (var query = new NpgsqlCommand("""
            SELECT r.id,r.device_id,r.details,
              (SELECT p.details FROM check_runs p WHERE p.device_id=r.device_id AND p.started_at<r.started_at
               ORDER BY p.started_at DESC,p.received_at DESC LIMIT 1),
              (SELECT p.details FROM check_runs p WHERE p.device_id=r.device_id AND p.started_at<r.started_at
               AND (jsonb_typeof(p.details->'Full'->'Events')='array' OR jsonb_typeof(p.details->'Events')='array')
               ORDER BY p.started_at DESC,p.received_at DESC LIMIT 1)
            FROM check_runs r WHERE EXISTS (SELECT 1 FROM monitor_alerts a WHERE a.run_id=r.id AND a.detected_at>now()-interval '90 days')
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                runs.Add((reader.GetGuid(0), reader.GetGuid(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Parse(reader.GetString(4))));
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var run in runs)
        {
            await using var hide = new NpgsqlCommand("UPDATE monitor_alerts SET visible=false WHERE run_id=@run", connection, transaction);
            hide.Parameters.AddWithValue("run", run.Id);
            await hide.ExecuteNonQueryAsync();
            foreach (var issue in Monitoring.NewIssues(run.Report, run.Previous, run.Events, includeWarnings: true))
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO monitor_alerts(device_id,run_id,category,identity,title,detail,severity,detected_at,visible)
                    SELECT @device,@run,@category,@identity,@title,@detail,@severity,received_at,true FROM check_runs WHERE id=@run
                    ON CONFLICT(run_id,category,identity) DO UPDATE SET title=excluded.title,detail=excluded.detail,severity=excluded.severity,visible=true
                    """, connection, transaction);
                insert.Parameters.AddWithValue("device", run.Device);
                insert.Parameters.AddWithValue("run", run.Id);
                insert.Parameters.AddWithValue("category", issue.Category);
                insert.Parameters.AddWithValue("identity", issue.Identity);
                insert.Parameters.AddWithValue("title", issue.Title);
                insert.Parameters.AddWithValue("detail", issue.Detail);
                insert.Parameters.AddWithValue("severity", issue.Severity);
                await insert.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
