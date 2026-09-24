using System.Globalization;
using System.Text.Json;
using ITSeti.Maintenance.Core;
using Microsoft.Data.Sqlite;

namespace ITSeti.Maintenance.Infrastructure;

public sealed class SqliteHistoryStore(string databasePath) : IHistoryStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS runs (
                    id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, schema_version INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_runs_started ON runs(started_utc DESC);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public Task SaveAsync(DiagnosticSnapshot snapshot) => SaveManyAsync([snapshot]);

    public Task SaveManyAsync(IReadOnlyCollection<DiagnosticSnapshot> snapshots) => Task.Run(() =>
    {
        if (snapshots.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO runs(id, started_utc, schema_version, payload) VALUES ($id, $started, 1, $payload)";
        command.Transaction = transaction;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var started = command.Parameters.Add("$started", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        foreach (var snapshot in snapshots)
        {
            id.Value = snapshot.Id.ToString();
            started.Value = snapshot.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            payload.Value = JsonSerializer.Serialize(snapshot);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    });

    public Task<IReadOnlyList<DiagnosticSnapshot>> GetRecentAsync(int limit = 100) => Task.Run<IReadOnlyList<DiagnosticSnapshot>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM runs WHERE schema_version = 1 ORDER BY started_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var rows = new List<DiagnosticSnapshot>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<DiagnosticSnapshot>(reader.GetString(0));
            if (item is not null) rows.Add(item);
        }
        return rows;
    });
}
