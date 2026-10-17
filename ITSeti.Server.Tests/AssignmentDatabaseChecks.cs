using ITSeti.Server;
using Npgsql;

internal static class AssignmentDatabaseChecks
{
    public static async Task RunAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO companies(id,name,active) VALUES(7001,'Single site',true),(7002,'Multiple sites',true),(7003,'Other tenant',true);
            INSERT INTO service_objects(id,company_id,name,active) VALUES
              (7011,7001,'Only site',true),(7012,7001,'Inactive',false),
              (7021,7002,'Office',true),(7022,7002,'Warehouse',true),(7031,7003,'Foreign office',true);
            INSERT INTO devices(id,computer_name,inventory_number,company_id,service_object_id,api_key_hash,last_ip,created_at,last_seen_at) VALUES
              ('00000000-0000-0000-0000-000000007001','Single', '7001',7001,NULL,'assignment-single','203.0.113.10',now(),now()),
              ('00000000-0000-0000-0000-000000007002','Target', '7002',7002,NULL,'assignment-target','203.0.113.10',now(),now()),
              ('00000000-0000-0000-0000-000000007003','Peer', '7003',7002,7021,'assignment-peer','203.0.113.10',now(),now()),
              ('00000000-0000-0000-0000-000000007004','Foreign', '7004',7003,7031,'assignment-foreign','203.0.113.10',now(),now()),
              ('00000000-0000-0000-0000-000000007005','Old peer', '7005',7002,7022,'assignment-old','203.0.113.10',now(),now()-interval '121 days');
            INSERT INTO okdesk_equipment(id,normalized_inventory_number,company_id,service_object_id)
            VALUES(70001,'7002',7002,7021);
            """, connection, transaction)) await seed.ExecuteNonQueryAsync();

        async Task Expect(IpAssignment input, string? ip, long? expectedSite, string reason)
        {
            var actual = await SiteAssignmentResolver.CompleteAsync(connection,input,ip,
                Guid.Parse("00000000-0000-0000-0000-000000007002"),default,transaction);
            if (actual.CompanyId != input.CompanyId || actual.SiteId != expectedSite) throw new Exception(reason);
        }
        await Expect(new(7001,null),null,7011,"A single active site was not selected without IP.");
        await Expect(new(7002,null),"203.0.113.10",7021,"IP did not fill the site within the already known company.");
        await Expect(new(7002,null),"203.0.113.99",null,"Unknown IP guessed a site.");
        await Expect(new(7002,7022),"203.0.113.10",7022,"IP replaced an explicit existing site.");
        var inventory = await InventoryAssignmentResolver.ResolveAsync(connection,"7002",default,transaction);
        if (inventory != new IpAssignment(7002,7021)) throw new Exception("A site-less device masked matching equipment assignment.");

        await using (var update = new NpgsqlCommand("""
            UPDATE devices SET last_seen_at=now() WHERE api_key_hash='assignment-old';
            """,connection,transaction)) await update.ExecuteNonQueryAsync();
        await Expect(new(7002,null),"203.0.113.10",null,"Conflicting sites at the same IP were guessed.");
        await using (var update = new NpgsqlCommand("""
            UPDATE devices SET service_object_id=7021 WHERE api_key_hash='assignment-target';
            UPDATE service_objects SET name='New office name',address='New address' WHERE id=7021;
            """,connection,transaction)) await update.ExecuteNonQueryAsync();
        await using (var query = new NpgsqlCommand("SELECT service_object_id FROM devices WHERE api_key_hash='assignment-target'",connection,transaction))
            if ((long)(await query.ExecuteScalarAsync())! != 7021) throw new Exception("Changing an address changed the device site ID.");
        await transaction.RollbackAsync();
        Console.WriteLine("PASS PostgreSQL assignment: single active site, same-company IP, stale/foreign peers, conflicts, explicit site preservation, inventory enrichment and stable address ID");
    }
}
