using Npgsql;

namespace ITSeti.Server;

public sealed class TicketDispatchService(NpgsqlDataSource database, IOkdeskClient okdesk,
    ILogger<TicketDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var connection = await database.OpenConnectionAsync(stoppingToken))
        await using (var stale = new NpgsqlCommand("""
            UPDATE ticket_requests SET state='unknown',last_error='Сервер прервался во время отправки; проверьте Okdesk вручную.',updated_at=now()
            WHERE state='sending'
            """, connection))
            await stale.ExecuteNonQueryAsync(stoppingToken);
        await using (var connection = await database.OpenConnectionAsync(stoppingToken))
        await using (var stale = new NpgsqlCommand("UPDATE ticket_messages SET state='unknown',last_error='Отправка прервалась; проверьте Okdesk вручную.',updated_at=now() WHERE state='sending'", connection))
            await stale.ExecuteNonQueryAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (okdesk.CanSendTickets && await SendNextAsync(stoppingToken)) continue;
                if (okdesk.CanSendComments && await SendNextMessageAsync(stoppingToken)) continue;
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Ticket dispatch loop failed: {ErrorType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
        }
    }

    private async Task<bool> SendNextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Guid requestId;
        OkdeskIssue issue;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using var claim = new NpgsqlCommand("""
                SELECT request_id,company_id,service_object_id,title,description,service_code
                FROM ticket_requests WHERE state='queued' ORDER BY created_at,request_id
                LIMIT 1 FOR UPDATE SKIP LOCKED
                """, connection, transaction);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            requestId = reader.GetGuid(0);
            issue = TicketServices.Build(reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
            await reader.DisposeAsync();
            await using var mark = new NpgsqlCommand("UPDATE ticket_requests SET state='sending',updated_at=now() WHERE request_id=@id", connection, transaction);
            mark.Parameters.AddWithValue("id", requestId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            var attachments = await LoadAttachmentsAsync(connection, requestId, null, cancellationToken);
            var issueId = await okdesk.CreateIssueAsync(issue, cancellationToken, attachments);
            await SetStateAsync(connection, requestId, "created", issueId, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var rejected = ex is OkdeskApiException api && api.StatusCode is >= 400 and < 500 and not 429;
            var error = ex is OkdeskApiException response
                ? $"Okdesk вернул HTTP {response.StatusCode}."
                : "Ответ Okdesk не подтверждён; проверьте наличие заявки вручную.";
            logger.LogWarning("Ticket {RequestId} dispatch failed: {Type}, HTTP {Status}", requestId,
                ex.GetType().Name, (ex as OkdeskApiException)?.StatusCode);
            await SetStateAsync(connection, requestId, rejected ? "rejected" : "unknown", null, error, CancellationToken.None);
        }
        return true;
    }

    private async Task<bool> SendNextMessageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Guid messageId, requestId;
        long issueId;
        string content;
        bool isPublic;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await using var claim = new NpgsqlCommand("""
                SELECT m.id,m.request_id,r.okdesk_issue_id,m.content,m.is_public FROM ticket_messages m
                JOIN ticket_requests r ON r.request_id=m.request_id
                WHERE m.state='queued' AND r.state='created'
                ORDER BY m.created_at,m.id LIMIT 1 FOR UPDATE OF m SKIP LOCKED
                """, connection, transaction);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            messageId = reader.GetGuid(0);
            requestId = reader.GetGuid(1);
            issueId = reader.GetInt64(2);
            content = reader.GetString(3);
            isPublic = reader.GetBoolean(4);
            await reader.DisposeAsync();
            await using var mark = new NpgsqlCommand("UPDATE ticket_messages SET state='sending',updated_at=now() WHERE id=@id", connection, transaction);
            mark.Parameters.AddWithValue("id", messageId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        try
        {
            var attachments = await LoadAttachmentsAsync(connection, requestId, messageId, cancellationToken);
            var commentId = await okdesk.AddCommentAsync(issueId, content, attachments, cancellationToken, isPublic);
            await using var update = new NpgsqlCommand("UPDATE ticket_messages SET state='created',okdesk_comment_id=@comment,updated_at=now() WHERE id=@id AND state='sending'", connection);
            update.Parameters.AddWithValue("comment", commentId);
            update.Parameters.AddWithValue("id", messageId);
            await update.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            var rejected = ex is OkdeskApiException api && api.StatusCode is >= 400 and < 500 and not 429;
            await using var update = new NpgsqlCommand("UPDATE ticket_messages SET state=@state,last_error=@error,updated_at=now() WHERE id=@id AND state='sending'", connection);
            update.Parameters.AddWithValue("state", rejected ? "rejected" : "unknown");
            update.Parameters.AddWithValue("error", rejected ? "Okdesk отклонил сообщение." : "Ответ Okdesk не подтверждён; проверьте вручную.");
            update.Parameters.AddWithValue("id", messageId);
            await update.ExecuteNonQueryAsync(CancellationToken.None);
            logger.LogWarning("Ticket message {MessageId} failed: {Type}", messageId, ex.GetType().Name);
        }
        return true;
    }

    private static async Task<IReadOnlyList<TicketAttachment>> LoadAttachmentsAsync(NpgsqlConnection connection,
        Guid requestId, Guid? messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT file_name,content_type,bytes FROM ticket_attachments
            WHERE request_id=@request AND message_id IS NOT DISTINCT FROM @message ORDER BY created_at,id
            """, connection);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.Add("message", NpgsqlTypes.NpgsqlDbType.Uuid).Value = (object?)messageId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<TicketAttachment>();
        while (await reader.ReadAsync(cancellationToken))
            files.Add(new(Guid.Empty, reader.GetString(0), reader.GetString(1), (byte[])reader.GetValue(2)));
        return files;
    }

    private static async Task SetStateAsync(NpgsqlConnection connection, Guid requestId, string state, long? issueId,
        string? error, CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE ticket_requests SET state=@state,okdesk_issue_id=@issue,last_error=@error,updated_at=now()
            WHERE request_id=@id AND state='sending'
            """, connection);
        update.Parameters.AddWithValue("id", requestId);
        update.Parameters.AddWithValue("state", state);
        update.Parameters.Add("issue", NpgsqlTypes.NpgsqlDbType.Bigint).Value = (object?)issueId ?? DBNull.Value;
        update.Parameters.Add("error", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)error ?? DBNull.Value;
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
