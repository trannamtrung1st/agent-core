using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantArtifactCommit(Guid OperationId, Guid RunId, Guid Generation, long ExpectedRunRevision,
    string SourceCallId, string ArgumentsJson, string ParametersHash, long ExpectedInstanceRevision, string ActionHash,
    PreparedArtifactSource? Source = null, ITenantWorkspaceArtifactSource? SourceContent = null);

// The same direct local effect/Run/security/audit receipt owner as preferences.
public interface ITenantArtifactOperationStore : ITenantDirectOperationStore
{
    ValueTask<TenantOperationReceipt?> TryCommitArtifactAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantArtifactCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}
