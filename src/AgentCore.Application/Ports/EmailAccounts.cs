using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Ports;

// Safe identity only. Protected authentication is resolved inside Infrastructure at use time.
public sealed record EmailAccountContext(Guid AgentInstanceId, Guid AccountId, string AccountRef, string DisplayName,
    long AccountRevision, Guid BindingId, long BindingRevision, EmailAccountProfile Profile);
public sealed record BoundEmailAccount(string AccountRef, string DisplayName, string EmailAddress, string Provider);
public interface IEmailProviderFactory
{
    bool IsEnabled { get; }
    bool IsSendEnabled { get; }
    IEmailProvider Create(EmailAccountContext account);
    ValueTask<EmailConnectionTest> TestAsync(Guid accountId, long expectedRevision, string protocol, CancellationToken ct = default);
}
public sealed record EmailConnectionTest(string Protocol, string Status, DateTimeOffset ObservedAtUtc, string? Error);
public sealed record StoredEmailDraft(Guid AgentInstanceId, Guid SessionId, EmailDraftSnapshot Draft, DateTimeOffset CreatedAtUtc);
public sealed record EmailSendOperation(string DraftId, Guid AccountId, long DraftRevision, Guid ApprovalId,
    string ActionHash, EmailSendResult? Result, DateTimeOffset ClaimedAtUtc);
public sealed record EmailAccountSelection(Guid RequestId, Guid AgentInstanceId, Guid SessionId, Guid SourceRunId,
    string Target, DateTimeOffset ExpiresAtUtc, string Status = "pending", long? InstanceRevision = null, Guid? SelectedAccountId = null, string? SelectedAccountRef = null);
public interface IEmailStore
{
    ValueTask CreateSelectionAsync(EmailAccountSelection request, CancellationToken ct = default);
    ValueTask<EmailAccountSelection?> GetSelectionAsync(Guid requestId, CancellationToken ct = default);
    ValueTask<bool> UpdateSelectionAsync(EmailAccountSelection request, string expectedStatus, CancellationToken ct = default);
    ValueTask CreateDraftAsync(StoredEmailDraft draft, CancellationToken ct = default);
    ValueTask<StoredEmailDraft?> GetDraftAsync(string draftId, CancellationToken ct = default);
    // Unique immutable draft version, independent of Run/worker/approval identity. Null result is uncertain, never retryable.
    ValueTask<bool> TryClaimSendAsync(EmailSendOperation operation, CancellationToken ct = default);
    ValueTask<EmailSendOperation?> GetSendAsync(string draftId, CancellationToken ct = default);
    ValueTask CompleteSendAsync(string draftId, EmailSendResult result, CancellationToken ct = default);
}
