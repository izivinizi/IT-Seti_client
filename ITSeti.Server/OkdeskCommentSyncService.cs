using Npgsql;

namespace ITSeti.Server;

public sealed class OkdeskCommentSyncService(NpgsqlDataSource database, OkdeskApiClient okdesk,
    ILogger<OkdeskCommentSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (okdesk.Configured) await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Okdesk comment sync failed: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT request_id,okdesk_issue_id FROM ticket_requests
            WHERE state='created' AND okdesk_issue_id IS NOT NULL
            ORDER BY updated_at DESC LIMIT 100
            """, connection);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<(Guid Id, long IssueId)>();
        while (await reader.ReadAsync(cancellationToken)) tickets.Add((reader.GetGuid(0), reader.GetInt64(1)));
        await reader.DisposeAsync();
        foreach (var ticket in tickets)
        {
            try
            {
                var comments = await okdesk.GetCommentsAsync(ticket.IssueId, cancellationToken);
                foreach (var comment in comments)
                {
                    if (comment.AuthorType.Equals("employee", StringComparison.OrdinalIgnoreCase) &&
                        okdesk.CommentAuthorId.HasValue && comment.AuthorId == okdesk.CommentAuthorId) continue;
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO ticket_messages(id,request_id,direction,content,state,okdesk_comment_id)
                        VALUES(@id,@request,'engineer',@content,'created',@remote)
                        ON CONFLICT DO NOTHING
                        """, connection);
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("request", ticket.Id);
                    insert.Parameters.AddWithValue("content", comment.Content);
                    insert.Parameters.AddWithValue("remote", comment.Id);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            catch (Exception ex) { logger.LogWarning("Comment sync failed for {IssueId}: {Type}", ticket.IssueId, ex.GetType().Name); }
        }
    }
}
