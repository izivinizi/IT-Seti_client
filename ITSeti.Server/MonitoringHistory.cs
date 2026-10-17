using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ITSeti.Server;

public static class MonitoringHistory
{
    private const string MigrationKey = "monitor-alerts-2026-10-16-ui";
    private const int BatchSize = 25;

    public static async Task ReconcileAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        // One server at a time; the lock is released automatically if the process exits.
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_lock(70419321)", connection))
            await guard.ExecuteNonQueryAsync();
        try
        {
            await InitializeMigrationAsync(connection);
            while (true)
            {
                var state = await ReadStateAsync(connection);
                if (state is null) return;
                var runs = await ReadBatchAsync(connection, state.Value.Cutoff, state.Value.Cursor);
                await using var transaction = await connection.BeginTransactionAsync();
                if (runs.Count == 0)
                {
                    await using var complete = new NpgsqlCommand(
                        "UPDATE maintenance_migrations SET completed_at=now() WHERE key=@key", connection, transaction);
                    complete.Parameters.AddWithValue("key", MigrationKey);
                    await complete.ExecuteNonQueryAsync();
                    await transaction.CommitAsync();
                    return;
                }
                foreach (var run in runs)
                {
                    await using var hide = new NpgsqlCommand(
                        "UPDATE monitor_alerts SET visible=false WHERE run_id=@run AND detected_at<@cutoff", connection, transaction);
                    hide.Parameters.AddWithValue("run", run.Id);
                    hide.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = state.Value.Cutoff;
                    await hide.ExecuteNonQueryAsync();
                    foreach (var issue in Monitoring.AlertsForReport(run.Report, run.Previous, run.Events))
                    {
                        await using var insert = new NpgsqlCommand("""
                            INSERT INTO monitor_alerts(device_id,run_id,category,identity,title,detail,severity,detected_at,visible)
                            SELECT @device,@run,@category,@identity,@title,@detail,@severity,received_at,true FROM check_runs WHERE id=@run
                            ON CONFLICT(run_id,category,identity) DO UPDATE SET title=excluded.title,
                              detail=excluded.detail,severity=excluded.severity,visible=true
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
                await using var checkpoint = new NpgsqlCommand(
                    "UPDATE maintenance_migrations SET cursor=@cursor WHERE key=@key", connection, transaction);
                checkpoint.Parameters.AddWithValue("cursor", runs[^1].Id);
                checkpoint.Parameters.AddWithValue("key", MigrationKey);
                await checkpoint.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(70419321)", connection);
            await release.ExecuteNonQueryAsync();
        }
    }

    private static async Task InitializeMigrationAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var schema = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS maintenance_migrations (
                key text PRIMARY KEY, cutoff timestamptz NOT NULL,
                cursor uuid, completed_at timestamptz);
            CREATE TABLE IF NOT EXISTS monitor_alerts_backup_20261016_ui AS SELECT * FROM monitor_alerts WHERE false;
            """, connection, transaction))
            await schema.ExecuteNonQueryAsync();
        await using (var start = new NpgsqlCommand(
            "INSERT INTO maintenance_migrations(key,cutoff) VALUES(@key,now()) ON CONFLICT DO NOTHING RETURNING key", connection, transaction))
        {
            start.Parameters.AddWithValue("key", MigrationKey);
            if (await start.ExecuteScalarAsync() is not null)
            {
                await using var backup = new NpgsqlCommand("""
                    INSERT INTO monitor_alerts_backup_20261016_ui
                    SELECT a.* FROM monitor_alerts a,maintenance_migrations m
                    WHERE m.key=@key AND a.detected_at>=m.cutoff-interval '90 days' AND a.detected_at<m.cutoff
                    """, connection, transaction);
                backup.Parameters.AddWithValue("key", MigrationKey);
                await backup.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    private static async Task<(DateTime Cutoff, Guid? Cursor)?> ReadStateAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT cutoff,cursor FROM maintenance_migrations WHERE key=@key AND completed_at IS NULL", connection);
        command.Parameters.AddWithValue("key", MigrationKey);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetGuid(1))
            : null;
    }

    private static async Task<List<(Guid Id, Guid Device, JsonElement Report, JsonElement? Previous, JsonElement? Events)>>
        ReadBatchAsync(NpgsqlConnection connection, DateTime cutoff, Guid? cursor)
    {
        var runs = new List<(Guid, Guid, JsonElement, JsonElement?, JsonElement?)>();
        await using var query = new NpgsqlCommand("""
            SELECT r.id,r.device_id,r.details,
              (SELECT p.details FROM check_runs p WHERE p.device_id=r.device_id AND p.started_at<r.started_at
               ORDER BY p.started_at DESC,p.received_at DESC LIMIT 1),
              (SELECT p.details FROM check_runs p WHERE p.device_id=r.device_id AND p.started_at<r.started_at
               AND (jsonb_typeof(p.details->'Full'->'Events')='array' OR jsonb_typeof(p.details->'Events')='array')
               ORDER BY p.started_at DESC,p.received_at DESC LIMIT 1)
            FROM check_runs r WHERE r.received_at>=@cutoff-interval '90 days' AND r.received_at<@cutoff
              AND (@cursor IS NULL OR r.id>@cursor)
            ORDER BY r.id LIMIT @limit
            """, connection);
        query.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;
        query.Parameters.Add("cursor", NpgsqlDbType.Uuid).Value = (object?)cursor ?? DBNull.Value;
        query.Parameters.AddWithValue("limit", BatchSize);
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            runs.Add((reader.GetGuid(0), reader.GetGuid(1), Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4))));
        return runs;
    }

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
