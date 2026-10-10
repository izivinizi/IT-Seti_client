using Microsoft.AspNetCore.Antiforgery;
using Npgsql;
using NpgsqlTypes;

namespace ITSeti.Server;

public static class PortalOperations
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/computer-tree", async (NpgsqlDataSource db, long? companyId) =>
        {
            await using var connection = await db.OpenConnectionAsync();
            await using var query = new NpgsqlCommand(companyId.HasValue ? """
                SELECT s.id,s.name,s.address,count(d.id) FROM service_objects s
                LEFT JOIN devices d ON d.service_object_id=s.id AND d.company_id=@company
                WHERE s.company_id=@company AND s.active GROUP BY s.id,s.name,s.address
                UNION ALL SELECT NULL,'Без объекта',NULL,count(*) FROM devices WHERE company_id=@company AND service_object_id IS NULL HAVING count(*)>0
                ORDER BY 2
                """ : """
                SELECT c.id,c.name,NULL::text,count(d.id) FROM companies c LEFT JOIN devices d ON d.company_id=c.id
                WHERE c.active GROUP BY c.id,c.name ORDER BY c.name
                """, connection);
            if (companyId.HasValue) query.Parameters.AddWithValue("company", companyId.Value);
            var items = new List<object>();
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) items.Add(new { id = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                name = reader.GetString(1), address = reader.IsDBNull(2) ? null : reader.GetString(2), computers = reader.GetInt64(3) });
            return Results.Ok(new { level = companyId.HasValue ? "sites" : "companies", items });
        });
        admin.MapGet("/tickets/{id:guid}/conversation", async (Guid id, NpgsqlDataSource db) =>
        {
            await using var connection = await db.OpenConnectionAsync();
            await using var query = new NpgsqlCommand("SELECT id,content,is_public,state,created_at,last_error FROM ticket_messages WHERE request_id=@id ORDER BY created_at,id LIMIT 500", connection);
            query.Parameters.AddWithValue("id", id);
            await using var reader = await query.ExecuteReaderAsync();
            var rows = new List<object>();
            while (await reader.ReadAsync()) rows.Add(new { id=reader.GetGuid(0), content=reader.GetString(1), isPublic=reader.GetBoolean(2), state=reader.GetString(3), createdAt=reader.GetDateTime(4), error=reader.IsDBNull(5) ? null : reader.GetString(5) });
            return Results.Ok(rows);
        });
        admin.MapPost("/tickets/{id:guid}/reply", async (Guid id, ReplyRequest body, HttpContext context, IAntiforgery csrf, NpgsqlDataSource db) =>
        {
            await csrf.ValidateRequestAsync(context);
            if (body.Id == Guid.Empty || string.IsNullOrWhiteSpace(body.Content) || body.Content.Length > 5000)
                return Results.BadRequest(new { error="Укажите текст ответа до 5000 символов." });
            await using var connection = await db.OpenConnectionAsync();
            await using var query = new NpgsqlCommand("""
                INSERT INTO ticket_messages(id,request_id,direction,content,is_public,state)
                SELECT @message,@id,'engineer',@text,@public,'queued' WHERE EXISTS(SELECT 1 FROM ticket_requests WHERE request_id=@id)
                ON CONFLICT(id) DO NOTHING
                """, connection);
            query.Parameters.AddWithValue("message", body.Id);
            query.Parameters.AddWithValue("id", id);
            query.Parameters.AddWithValue("text", body.Content.Trim());
            query.Parameters.AddWithValue("public", body.IsPublic);
            await query.ExecuteNonQueryAsync();
            await using var check = new NpgsqlCommand("SELECT request_id,content,is_public FROM ticket_messages WHERE id=@id", connection);
            check.Parameters.AddWithValue("id", body.Id);
            await using var reader = await check.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            if (reader.GetGuid(0) != id || reader.GetString(1) != body.Content.Trim() || reader.GetBoolean(2) != body.IsPublic)
                return Results.Conflict(new { error="Идентификатор ответа уже использован." });
            return Results.Ok(new { queued=true });
        });
        admin.MapPost("/tickets/{id:guid}/action", async (Guid id, ActionRequest body, HttpContext context, IAntiforgery csrf, NpgsqlDataSource db) =>
        {
            await csrf.ValidateRequestAsync(context);
            if (body.Id == Guid.Empty || body.Target is not ("completed" or "cancelled")) return Results.BadRequest();
            await using var connection = await db.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var query = new NpgsqlCommand("""
                INSERT INTO ticket_actions(id,request_id,target) SELECT @action,@id,@target
                WHERE EXISTS(SELECT 1 FROM ticket_requests WHERE request_id=@id) ON CONFLICT(id) DO NOTHING
                """, connection, transaction);
            query.Parameters.AddWithValue("action", body.Id);
            query.Parameters.AddWithValue("id", id);
            query.Parameters.AddWithValue("target", body.Target);
            await query.ExecuteNonQueryAsync();
            await using var check = new NpgsqlCommand("SELECT request_id,target FROM ticket_actions WHERE id=@id", connection, transaction);
            check.Parameters.AddWithValue("id", body.Id);
            await using var reader = await check.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            if (reader.GetGuid(0) != id || reader.GetString(1) != body.Target) return Results.Conflict();
            await reader.DisposeAsync();
            await using var update = new NpgsqlCommand("UPDATE ticket_requests SET workflow_state=@target,updated_at=now() WHERE request_id=@id", connection, transaction);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("target", body.Target);
            await update.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return Results.Ok(new { queued=true });
        });
    }
}
public sealed record ReplyRequest(Guid Id, string Content, bool IsPublic);
public sealed record ActionRequest(Guid Id, string Target);

public sealed class TicketActionDispatcher(NpgsqlDataSource db, IOkdeskClient okdesk, ILogger<TicketActionDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await using (var connection = await db.OpenConnectionAsync(cancellationToken))
        await using (var reset = new NpgsqlCommand("UPDATE ticket_actions SET state='unknown',last_error='Ответ Okdesk не подтверждён; требуется проверка статуса.' WHERE state='sending'", connection))
            await reset.ExecuteNonQueryAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { if (okdesk.CanChangeStatus) await DispatchAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Ticket action failed: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT a.id,a.target,t.okdesk_issue_id FROM ticket_actions a JOIN ticket_requests t ON t.request_id=a.request_id
            WHERE a.state='queued' AND a.updated_at < now()-interval '15 seconds' AND t.okdesk_issue_id IS NOT NULL
            ORDER BY a.created_at,a.id LIMIT 1 FOR UPDATE OF a SKIP LOCKED
            """, connection, transaction);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id=reader.GetGuid(0); var target=reader.GetString(1); var issue=reader.GetInt64(2);
        await reader.DisposeAsync();
        await using var mark = new NpgsqlCommand("UPDATE ticket_actions SET state='sending',updated_at=now() WHERE id=@id", connection, transaction);
        mark.Parameters.AddWithValue("id", id);
        await mark.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        string state="created"; string? error=null;
        try { await okdesk.SetIssueStatusAsync(issue, target, cancellationToken); }
        catch (OkdeskApiException ex) { state=ex.StatusCode == 429 ? "queued" : ex.StatusCode is >=400 and <500 ? "rejected" : "unknown"; error=$"Okdesk: HTTP {ex.StatusCode}"; }
        catch (Exception) { state="unknown"; error="Статус не подтверждён Okdesk; требуется проверка перед повторной отправкой."; }
        await using var update = new NpgsqlCommand("UPDATE ticket_actions SET state=@state,last_error=@error,updated_at=now() WHERE id=@id", connection);
        update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("state", state);
        update.Parameters.Add("error", NpgsqlDbType.Text).Value=(object?)error ?? DBNull.Value;
        await update.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
