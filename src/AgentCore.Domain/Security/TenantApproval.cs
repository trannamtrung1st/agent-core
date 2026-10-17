using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Security;

public enum TenantApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }
public enum OperationGrantStatus { Available, Claimed, Consumed, Revoked, Indeterminate }

// Core prepares this closed action shape; it contains no credential or bearer authority.
public sealed record PreparedPersonaOperation(Guid OperationId, Guid InstanceId, AgentIdentity Persona,
    long ExpectedInstanceRevision, long ExpectedPersonaRevision, string CanonicalActionJson, string ActionHash);

public sealed record TenantApprovalRequest(Guid RequestId, Guid TenantId, Guid RunId, Guid SessionId,
    Guid RootActivationId, Guid ExecutorPrincipalId, Guid? OriginalInitiatorPrincipalId,
    ApprovalKind Kind, PreparedPersonaOperation Operation, TenantApprovalStatus Status,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    public const string Permission = "agents.update";
    public const string ResourceType = "agent";
    public string ResourceId => Operation.InstanceId.ToString("D");
    public string Fingerprint => Operation.ActionHash;

    public TenantApprovalRequest Decide(TenantApprovalStatus decision, long expectedRevision, DateTimeOffset at)
    {
        if (Status != TenantApprovalStatus.Pending || Revision != expectedRevision)
            throw new InvalidOperationException("Approval decision is stale or already terminal.");
        if (decision is not (TenantApprovalStatus.Approved or TenantApprovalStatus.Rejected or TenantApprovalStatus.Cancelled or TenantApprovalStatus.Expired)
            || at.Offset != TimeSpan.Zero || at < CreatedAt || decision == TenantApprovalStatus.Expired != (at >= ExpiresAt))
            throw new ArgumentException("Approval decision or time is invalid.");
        return this with { Status = decision, Revision = checked(Revision + 1) };
    }
}

public sealed record TenantApprovalDecision(Guid DecisionId, Guid RequestId, Guid TenantId, Guid HumanPrincipalId,
    TenantApprovalStatus Status, string ActionHash, long RequestRevision, DateTimeOffset DecidedAt);

// The immutable request binds exact executor, permission, target, hash and witnesses. Never a RoleAssignment.
public sealed record TenantOperationGrant(Guid GrantId, Guid RequestId, Guid TenantId, Guid ExecutorPrincipalId,
    Guid OperationId, string ActionHash, string EffectIdempotencyKey, OperationGrantStatus Status, long Revision, DateTimeOffset ExpiresAt);

public sealed record ApprovalProcessedSignal(Guid SignalId, Guid TenantId, Guid RequestId, Guid RunId,
    long DecisionRevision, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt);
