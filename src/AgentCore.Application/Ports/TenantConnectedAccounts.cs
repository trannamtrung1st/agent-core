using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public enum ConnectedAccountReadKind { EmailSearch, EmailMessage, DriveList, DriveFile, DriveContent }
public sealed record ConnectedAccountRead(ConnectedAccountReadKind Kind, string? Query = null, string? Id = null,
    string? Folder = null, string? Cursor = null, int Limit = 20, string? ExpectedVersion = null, int MaxBytes = 50 * 1024 * 1024);
public sealed record ConnectedAccountReadResult(EmailSearchResult? Mailbox = null, EmailMessageResult? Message = null,
    DriveFilePage? Files = null, DriveFile? File = null, byte[]? Bytes = null, ConnectedAccountOperationResult? Operation = null);
public sealed record ConnectedAccountOperationItem(Guid OperationId, string? FileId, string Status, string Evidence, DriveFile? File = null);
public sealed record ConnectedAccountOperationResult(Guid OperationId, string Tool, string RecordedOutcome,
    IReadOnlyList<ConnectedAccountOperationItem> Items, bool ReplayAllowed = false,
    string Limitation = "Read-only verification is separate from the original recorded outcome. No write was repeated; missing evidence never permits replay.");
public sealed record TenantConnectedAccount(Guid TenantId, Guid InstanceId, AgentCredentialBinding Binding, Credential Credential);
public sealed record ConnectedAccountAvailability(bool Available, string Status);
public sealed record TenantConnectedAccountView(string AccountRef, string DisplayName, string Purpose, string EmailAddress,
    string Status, bool ReadAvailable, long CredentialRevision, long BindingRevision);
public sealed record TenantConnectedAccountPage(IReadOnlyList<TenantConnectedAccountView> Items, int Total, int Page, int PageSize);

// A reader receives only canonical encrypted account state. Tokens and provider DTOs stay inside Infrastructure.
// Recheck is required around provider I/O; maintenance commits only normalized protected rotation/reconnect facts.
public interface ITenantConnectedAccountReader
{
    ConnectedAccountAvailability Availability(Credential account);
    ValueTask<ConnectedAccountReadResult> ReadAsync(TenantConnectedAccount account, ConnectedAccountRead request,
        Func<CancellationToken, ValueTask<Credential>> recheck,
        Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, CancellationToken ct = default);
}

// Native ports for one already authorized canonical account operation. The callbacks
// remain live for the lifetime of these adapters; this object carries no token.
public sealed record TenantConnectedAccountTransport(IEmailProvider? Email, IGoogleDriveProvider? Drive, bool Synthetic);
public interface ITenantConnectedAccountTransportFactory
{
    ConnectedAccountAvailability Availability(Credential account, bool send = false);
    ValueTask<TenantConnectedAccountTransport> OpenAsync(TenantConnectedAccount account,
        Func<CancellationToken, ValueTask<Credential>> recheck,
        Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance,
        bool send = false, CancellationToken ct = default);
}
public sealed record TenantConnectedAccountReadProof(Guid TenantId, Guid InstanceId, long InstanceRevision, Guid BindingId,
    long BindingRevision, Guid CredentialId, long CredentialRevision, long SecretRevision, CredentialPurpose Purpose,
    HumanSessionReference Human, long SecurityRevision, AuthorizationAudit Audit);
public interface ITenantConnectedAccountReadStore
{
    ValueTask<bool> TryRecordConnectedAccountReadAsync(TenantConnectedAccountReadProof proof, CancellationToken ct = default);
    ValueTask<bool> TryCommitRuntimeAccountReadAsync(TenantSecurityState next, long expectedSecurityRevision, TenantRuntimeAccountReadProof proof,
        CredentialOAuthCheck? maintenance, AuthorizationAudit audit, CancellationToken ct = default);
}
public sealed record TenantRuntimeAccountReadProof(TenantRuntimeFrame Frame, long RunRevision, Guid RequestId, Guid OperationId,
    long CredentialRevision, long SecretRevision);
