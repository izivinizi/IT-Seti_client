using Npgsql;

namespace ITSeti.Server;

public sealed class OkdeskCommentSyncService(NpgsqlDataSource database, IOkdeskClient okdesk,
    ILogger<OkdeskCommentSyncService> logger) : BackgroundService
{
    private Guid lastTicket;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (okdesk.CanReadComments) await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Okdesk comment sync failed: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT request_id,okdesk_issue_id FROM ticket_requests
            WHERE state='created' AND workflow_state='opened' AND okdesk_issue_id IS NOT NULL
            ORDER BY (request_id > @last) DESC,request_id LIMIT 100
            """, connection);
        query.Parameters.AddWithValue("last", lastTicket);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<(Guid Id, long IssueId)>();
        while (await reader.ReadAsync(cancellationToken)) tickets.Add((reader.GetGuid(0), reader.GetInt64(1)));
        await reader.DisposeAsync();
        foreach (var ticket in tickets)
        {
            lastTicket = ticket.Id;
            try
            {
                var comments = await okdesk.GetCommentsAsync(ticket.IssueId, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                // A previously public comment may have been made private in Okdesk.
                await using var hide = new NpgsqlCommand("""
                    UPDATE ticket_messages SET is_public=false
                    WHERE request_id=@request AND direction='engineer' AND okdesk_comment_id IS NOT NULL
                        AND NOT (okdesk_comment_id=ANY(@visible))
                    """, connection, transaction);
                hide.Parameters.AddWithValue("request", ticket.Id);
                hide.Parameters.AddWithValue("visible", comments.Select(c => c.Id).ToArray());
                await hide.ExecuteNonQueryAsync(cancellationToken);
                foreach (var comment in comments)
                {
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO ticket_messages(id,request_id,direction,content,state,okdesk_comment_id)
                        VALUES(@id,@request,'engineer',@content,'created',@remote)
                        ON CONFLICT (request_id,okdesk_comment_id) WHERE okdesk_comment_id IS NOT NULL
                        DO UPDATE SET content=excluded.content,is_public=true
                        WHERE ticket_messages.direction='engineer'
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("request", ticket.Id);
                    insert.Parameters.AddWithValue("content", comment.Content);
                    insert.Parameters.AddWithValue("remote", comment.Id);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) { logger.LogWarning("Comment sync failed for {IssueId}: {Type}", ticket.IssueId, ex.GetType().Name); }
        }
    }
}
