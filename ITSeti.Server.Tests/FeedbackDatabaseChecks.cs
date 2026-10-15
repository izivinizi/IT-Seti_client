using ITSeti.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

internal static class FeedbackDatabaseChecks
{
    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ITSETI_FEEDBACK_TEST_DATABASE")
            ?? throw new InvalidOperationException("Use a disposable local PostgreSQL database.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host != "127.0.0.1" || settings.Database?.StartsWith("itseti_feedback_") != true)
            throw new InvalidOperationException("These fixtures require a disposable local test database.");
        await using var db = new NpgsqlDataSourceBuilder(connectionString).Build();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["BootstrapAdminUser"] = "fixture", ["BootstrapAdminPassword"] = "fixture-only-password-2026"
        }).Build();
        await Database.InitializeAsync(db, config, new PasswordHasher<PortalAdmin>());
        await using var connection = await db.OpenConnectionAsync();
        var device = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO companies(id,name,active) VALUES(1,'Fixture 1',true),(2,'Fixture 2',true);
            INSERT INTO devices(id,computer_name,inventory_number,company_id,api_key_hash,created_at,last_seen_at)
            VALUES(@id,'Fixture PC','0553',1,'fixture-device-hash',now(),now());
            INSERT INTO okdesk_equipment(id,inventory_number,normalized_inventory_number,company_id,name)
            VALUES(501,'0553','553',1,'Fixture asset'),(502,'0553','553',2,'Another company');
            """, connection))
        { seed.Parameters.AddWithValue("id", device); await seed.ExecuteNonQueryAsync(); }
        var match = await TicketEquipmentResolver.ResolveAsync(connection, device, 1, default);
        if (!match.SequenceEqual([501L])) throw new Exception("The ticket did not resolve its unique asset by inventory/company.");
        await using (var duplicate = new NpgsqlCommand("INSERT INTO okdesk_equipment(id,normalized_inventory_number,company_id) VALUES(503,'553',1)", connection))
            await duplicate.ExecuteNonQueryAsync();
        if ((await TicketEquipmentResolver.ResolveAsync(connection, device, 1, default)).Length != 0)
            throw new Exception("An ambiguous duplicate asset was attached.");
        if ((await TicketEquipmentResolver.ResolveAsync(connection, Guid.NewGuid(), 1, default)).Length != 0)
            throw new Exception("An unknown PC received an equipment association.");
        await using (var save = new NpgsqlCommand("""
            INSERT INTO ticket_requests(request_id,device_id,company_id,title,description,service_code,payload_hash,state,equipment_ids)
            VALUES(@request,@device,1,'Fixture','Fixture ticket','525','fixture','queued',@equipment)
            """, connection))
        {
            save.Parameters.AddWithValue("request", Guid.NewGuid()); save.Parameters.AddWithValue("device", device);
            save.Parameters.Add("equipment", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = match;
            await save.ExecuteNonQueryAsync();
        }
        await Database.InitializeAsync(db, config, new PasswordHasher<PortalAdmin>());
        await using (var saved = new NpgsqlCommand("SELECT equipment_ids FROM ticket_requests LIMIT 1", connection))
            if (!(await saved.ExecuteScalarAsync() is long[] ids && ids.SequenceEqual(match)))
                throw new Exception("A schema migration erased the ticket asset snapshot.");
        Console.WriteLine("PASS PostgreSQL: idempotent schema, unique inventory/company equipment, ambiguity rejection, persisted ticket equipment");

        foreach (var test in new (string[]? Types, long Expected)[] { (null, 8), ([], 0), (["cpu", "events"], 2), (["temperature"], 1) })
        {
            await using var command = new NpgsqlCommand($"""
                SELECT count(*) FROM (VALUES
                  ('event','event:1','Windows',''), ('finding','cpu:load','CPU',''),
                  ('finding','memory:load','RAM',''), ('finding','cpu:temperature','CPU temp',''),
                  ('finding','hwinfo:rail-12v','PSU',''), ('finding','disk:health:fixture','SSD',''),
                  ('identity','rms-id','RMS',''), ('finding','something-else','Other',''))
                  a(category,identity,title,detail)
                WHERE @types IS NULL OR ({MonitoringKinds.Sql})=ANY(@types)
                """, connection);
            command.Parameters.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = (object?)test.Types ?? DBNull.Value;
            if ((long)(await command.ExecuteScalarAsync())! != test.Expected)
                throw new Exception("Monitoring type checkboxes filter the wrong records.");
        }
        Console.WriteLine("PASS PostgreSQL: all monitoring kinds, no selected kinds, selected subset, temperature before CPU classification");
    }
}
