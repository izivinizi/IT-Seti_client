using Npgsql;

namespace ITSeti.Server;

public sealed class OkdeskCommentSyncService(NpgsqlDataSource database, IOkdeskClient okdesk,
    ILogger<OkdeskCommentSyncService> logger) : BackgroundService
{
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
            WHERE state='created' AND okdesk_issue_id IS NOT NULL
              AND (workflow_state='opened' OR okdesk_synced_at IS NULL OR okdesk_synced_at < now()-interval '10 minutes')
            ORDER BY okdesk_synced_at NULLS FIRST,request_id LIMIT 100
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
                var remoteStatus = await okdesk.GetIssueStatusAsync(ticket.IssueId, cancellationToken);
                var downloaded = new List<(long CommentId, long RemoteId, TicketAttachment File)>();
                long downloadedSize = 0;
                foreach (var comment in comments)
                    foreach (var file in comment.Attachments ?? [])
                    {
                        await using var exists = new NpgsqlCommand("SELECT 1 FROM ticket_attachments WHERE request_id=@request AND okdesk_attachment_id=@remote", connection);
                        exists.Parameters.AddWithValue("request", ticket.Id);
                        exists.Parameters.AddWithValue("remote", file.Id);
                        if (await exists.ExecuteScalarAsync(cancellationToken) is not null) continue;
                        if (downloaded.Count >= 10 || downloadedSize + file.Size > 10 * 1024 * 1024) continue;
                        try
                        {
                            var bytes = await okdesk.DownloadAttachmentAsync(ticket.IssueId, file, cancellationToken);
                            var validated = TicketAttachments.Decode([new(file.FileName, Convert.ToBase64String(bytes))]).Single();
                            downloaded.Add((comment.Id, file.Id, validated));
                            downloadedSize += bytes.Length;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning("Attachment sync failed for issue {IssueId}, file {FileId}: {Type}", ticket.IssueId, file.Id, ex.GetType().Name); }
                    }
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
                        INSERT INTO ticket_messages(id,request_id,direction,content,state,okdesk_comment_id,okdesk_created_at)
                        VALUES(@id,@request,'engineer',@content,'created',@remote,@time)
                        ON CONFLICT (request_id,okdesk_comment_id) WHERE okdesk_comment_id IS NOT NULL
                        DO UPDATE SET content=excluded.content,is_public=true,okdesk_created_at=excluded.okdesk_created_at
                        WHERE ticket_messages.direction='engineer'
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("request", ticket.Id);
                    insert.Parameters.AddWithValue("content", comment.Content);
                    insert.Parameters.AddWithValue("remote", comment.Id);
                    insert.Parameters.Add("time", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = (object?)comment.CreatedAt?.UtcDateTime ?? DBNull.Value;
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                foreach (var file in downloaded)
                {
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO ticket_attachments(id,request_id,message_id,file_name,content_type,bytes,okdesk_attachment_id)
                        SELECT @id,@request,m.id,@name,@type,@bytes,@remote FROM ticket_messages m
                        WHERE m.request_id=@request AND m.okdesk_comment_id=@comment AND m.is_public
                        ON CONFLICT DO NOTHING
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("id", file.File.Id);
                    insert.Parameters.AddWithValue("request", ticket.Id);
                    insert.Parameters.AddWithValue("comment", file.CommentId);
                    insert.Parameters.AddWithValue("remote", file.RemoteId);
                    insert.Parameters.AddWithValue("name", file.File.FileName);
                    insert.Parameters.AddWithValue("type", file.File.ContentType);
                    insert.Parameters.AddWithValue("bytes", file.File.Bytes);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var status = new NpgsqlCommand("""
                    UPDATE ticket_requests SET okdesk_synced_at=now(),okdesk_status_name=COALESCE(@name,okdesk_status_name),
                        workflow_state=CASE WHEN @workflow IS NOT NULL AND NOT EXISTS
                            (SELECT 1 FROM ticket_actions WHERE request_id=@request AND state IN ('queued','sending'))
                            THEN @workflow ELSE workflow_state END,
                        updated_at=CASE WHEN @workflow IS NOT NULL AND workflow_state<>@workflow THEN now() ELSE updated_at END
                    WHERE request_id=@request
                    """, connection, transaction);
                status.Parameters.AddWithValue("request", ticket.Id);
                status.Parameters.Add("name", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)remoteStatus?.Name ?? DBNull.Value;
                status.Parameters.Add("workflow", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)remoteStatus?.WorkflowState ?? DBNull.Value;
                await status.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning("Comment sync failed for {IssueId}: {Type}", ticket.IssueId, ex.GetType().Name); }
        }
    }
}
