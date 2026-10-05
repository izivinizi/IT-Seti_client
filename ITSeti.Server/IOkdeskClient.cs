using System.Text.Json;

namespace ITSeti.Server;

public interface IOkdeskClient
{
    bool Configured { get; }
    bool CanSendTickets { get; }
    bool CanSendComments { get; }
    bool CanReadComments { get; }
    bool CanChangeStatus { get; }
    long? CommentAuthorId { get; }
    Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken cancellationToken);
    Task<long> CreateIssueAsync(OkdeskIssue issue, CancellationToken cancellationToken, IReadOnlyList<TicketAttachment>? attachments = null);
    Task<long> AddCommentAsync(long issueId, string text, IReadOnlyList<TicketAttachment> attachments, CancellationToken cancellationToken, bool isPublic = true);
    Task<IReadOnlyList<OkdeskComment>> GetCommentsAsync(long issueId, CancellationToken cancellationToken);
    Task<OkdeskIssueStatus?> GetIssueStatusAsync(long issueId, CancellationToken cancellationToken);
    Task<byte[]> DownloadAttachmentAsync(long issueId, OkdeskRemoteAttachment attachment, CancellationToken cancellationToken);
    Task SetIssueStatusAsync(long issueId, string target, CancellationToken cancellationToken);
}

public sealed record OkdeskIssueStatus(string WorkflowState, string Name);
public sealed record OkdeskRemoteAttachment(long Id, string FileName, string ContentType, long Size);
