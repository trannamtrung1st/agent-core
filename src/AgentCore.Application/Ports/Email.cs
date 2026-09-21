namespace AgentCore.Application.Ports;

public sealed record EmailSearchRequest(string Query, int Limit);

public sealed record EmailSearchItem(
    string MessageId,
    string ThreadId,
    string From,
    string Subject,
    DateTimeOffset Date,
    string Snippet);

public sealed record EmailSearchResult(IReadOnlyList<EmailSearchItem> Results, bool Truncated);

public sealed record EmailReadRequest(string MessageId);

public sealed record EmailAttachmentMetadata(string AttachmentId, string FileName, string ContentType, long ByteSize);

public sealed record EmailMessageResult(
    string MessageId,
    string ThreadId,
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset Date,
    string Subject,
    string Body,
    bool BodyTruncated,
    IReadOnlyList<EmailAttachmentMetadata> Attachments);

public sealed record EmailCreateDraftRequest(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string Body);

public sealed record EmailDraftSnapshot(
    string DraftId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string Body);

public sealed record EmailDraftResult(EmailDraftSnapshot Draft);

public sealed record EmailSendDraftRequest(string DraftId, EmailDraftSnapshot ApprovedDraft);

public enum EmailSendOutcome
{
    Sent,
    Failed,
    Indeterminate
}

public sealed record EmailSendResult(
    EmailSendOutcome Outcome,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage);

public interface IEmailProvider
{
    bool IsAvailable { get; }

    ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default);

    ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default);

    ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default);

    ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default);

    ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default);
}
