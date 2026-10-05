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
    Task SetIssueStatusAsync(long issueId, string target, CancellationToken cancellationToken);
}
