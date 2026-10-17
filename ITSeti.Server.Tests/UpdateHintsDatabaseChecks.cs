using ITSeti.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;

internal static class UpdateHintsDatabaseChecks
{
    public static async Task RunAsync(NpgsqlDataSource db)
    {
        var modern = Guid.NewGuid(); var legacy = Guid.NewGuid(); var tool = Guid.NewGuid();
        await using var connection = await db.OpenConnectionAsync();
        async Task Sql(string sql)
        {
            await using var command = new NpgsqlCommand(sql,connection);
            command.Parameters.AddWithValue("modern",modern); command.Parameters.AddWithValue("legacy",legacy);
            command.Parameters.AddWithValue("tool",tool); await command.ExecuteNonQueryAsync();
        }
        await Sql("""
            INSERT INTO packages(id,package_key,version,platform,sha256,size_bytes,download_name,storage_name,is_latest)
            VALUES(@modern,'application','2.4.11.0','windows','modern',1,'app.exe','fixture-modern',true),
              (@legacy,'application','2.4.9.0','win7','legacy',1,'win7.exe','fixture-legacy',true),
              (@tool,'test-tool','1.0','any','tool',1,'tool.exe','fixture-tool',true);
            """);
        try
        {
            var hints = new ClientUpdateHints(db,NullLogger<ClientUpdateHints>.Instance);
            var windows = await hints.GetAsync("windows",default);
            var win7 = await hints.GetAsync("win7",default);
            if (windows?.ApplicationVersion != "2.4.11.0" || win7?.ApplicationVersion != "2.4.9.0")
                throw new Exception("Update hints mixed application platforms.");
            if (windows.CatalogRevision.Length != 64 || windows.CatalogRevision == win7.CatalogRevision)
                throw new Exception("Catalog revision is missing or not platform-specific.");
            await Sql("UPDATE packages SET version='1.1' WHERE id=@tool");
            if ((await hints.GetAsync("windows",default)) != windows) throw new Exception("Hints bypassed the shared cache.");
            hints.Invalidate();
            if ((await hints.GetAsync("windows",default))?.CatalogRevision == windows.CatalogRevision ||
                (await hints.GetAsync("win7",default))?.CatalogRevision == win7.CatalogRevision)
                throw new Exception("A shared package update did not invalidate both catalog revisions.");
            await Sql("UPDATE packages SET is_latest=false WHERE id=@modern"); hints.Invalidate();
            if ((await hints.GetAsync("windows",default))?.ApplicationVersion is not null)
                throw new Exception("A retired modern package fell back to a Win7 package.");
            var context = new DefaultHttpContext();
            if (ClientUpdateHints.Platform(context.Request,"Microsoft Windows 7 Pro","6.1.7601") != "win7")
                throw new Exception("Legacy OS platform was not inferred.");
            context.Request.QueryString = new QueryString("?platform=unsupported");
            if (ClientUpdateHints.Platform(context.Request,"Windows 11",null) is not null)
                throw new Exception("Unsupported explicit platform produced update hints.");
            Console.WriteLine("PASS PostgreSQL update hints: platform isolation, cache/invalidation, catalog changes, retired package and OS detection");
        }
        finally { await Sql("DELETE FROM packages WHERE id IN (@modern,@legacy,@tool)"); }
    }
}
