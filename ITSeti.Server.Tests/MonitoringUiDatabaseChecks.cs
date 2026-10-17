using System.Text.Json;
using ITSeti.Server;
using Npgsql;

internal static class MonitoringUiDatabaseChecks
{
    public static async Task RunAsync(NpgsqlDataSource db)
    {
        var device=Guid.NewGuid(); var criticalRun=Guid.NewGuid(); var warningRun=Guid.NewGuid();
        var critical="""{"DiagnosticIssues":[{"Title":"Fixture disk failure","Detail":"Replace disk","Severity":"Critical"}]}""";
        var warning="""{"DiagnosticIssues":[{"Title":"Критическая загрузка CPU","Detail":"99%","Severity":"Critical"}]}""";
        using var report=JsonDocument.Parse(critical);
        var issue=Monitoring.CurrentIssues(report.RootElement).Single();
        await using var connection=await db.OpenConnectionAsync();
        await using(var seed=new NpgsqlCommand("""
            INSERT INTO devices(id,computer_name,company_id,api_key_hash,created_at)
            VALUES(@device,'UI-MIGRATION-FIXTURE',1,@key,now());
            INSERT INTO check_runs(id,device_id,started_at,kind,summary,details,received_at)
            VALUES(@critical,@device,now()-interval '2 hours','Полная','{}',@report::jsonb,now()-interval '2 hours'),
              (@warning,@device,now()-interval '1 hour','Постоянный мониторинг','{}',@warningReport::jsonb,now()-interval '1 hour');
            INSERT INTO monitor_alerts(device_id,run_id,category,identity,title,detail,severity,state,detected_at)
            VALUES(@device,@critical,@category,@identity,@title,@detail,1,'resolved',now()-interval '2 hours');
            """,connection))
        {
            seed.Parameters.AddWithValue("device",device);seed.Parameters.AddWithValue("key",device.ToString());
            seed.Parameters.AddWithValue("critical",criticalRun);seed.Parameters.AddWithValue("warning",warningRun);
            seed.Parameters.AddWithValue("report",critical);seed.Parameters.AddWithValue("warningReport",warning);
            seed.Parameters.AddWithValue("category",issue.Category);seed.Parameters.AddWithValue("identity",issue.Identity);
            seed.Parameters.AddWithValue("title",issue.Title);seed.Parameters.AddWithValue("detail",issue.Detail);
            await seed.ExecuteNonQueryAsync();
        }
        await MonitoringHistory.ReconcileAsync(db);await MonitoringHistory.ReconcileAsync(db);
        await using var query=new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM check_runs WHERE device_id=@device),
              (SELECT count(*) FROM monitor_alerts WHERE device_id=@device AND run_id=@critical AND state='resolved' AND visible),
              (SELECT count(*) FROM monitor_alerts WHERE device_id=@device AND run_id=@warning AND severity=2 AND visible),
              (SELECT count(*) FROM monitor_alerts_backup_20261016_ui WHERE device_id=@device)
            """,connection);
        query.Parameters.AddWithValue("device",device);query.Parameters.AddWithValue("critical",criticalRun);query.Parameters.AddWithValue("warning",warningRun);
        await using var reader=await query.ExecuteReaderAsync();await reader.ReadAsync();
        if(reader.GetInt64(0)!=2||reader.GetInt64(1)!=1||reader.GetInt64(2)!=1||reader.GetInt64(3)!=1)
            throw new Exception("UI monitoring migration lost reports/state, duplicated history, missed previously absent warnings, or omitted backup.");
        Console.WriteLine("PASS PostgreSQL monitoring migration: reports preserved, resolved state preserved, warnings backfilled without prior alerts, backup and idempotence");
    }
}
