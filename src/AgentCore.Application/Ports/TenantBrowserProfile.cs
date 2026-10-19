using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantBrowserAgentOwner(Guid TenantId, Guid AgentInstanceId, Guid ExecutorPrincipalId)
{
    public void Validate()
    { if (TenantId == Guid.Empty || AgentInstanceId == Guid.Empty || ExecutorPrincipalId == Guid.Empty) throw new ArgumentException("A complete Browser Agent owner is required."); }
    public bool SameAgent(TenantBrowserOwner owner) => this == owner.Agent;
}
public sealed record TenantBrowserProfileResult(Guid OperationId, string Status, string ReasonCode);
public sealed record TenantBrowserProfileObservation(Guid ProviderEpoch, string Witness, bool ProfilePresent, int LiveViews,
    bool Busy, Guid? InFlightOperation, TenantBrowserProfileResult? LastResult);
public interface ITenantBrowserProfileControl
{
    // Read existing handles and bounded directory metadata only; no binding, launch or content read.
    ValueTask<TenantBrowserProfileObservation> ReadTenantProfileAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default);
    ValueTask<TenantBrowserProfileResult> ResetTenantProfileAsync(TenantBrowserAgentOwner owner, Guid operationId, string witness,
        Func<Guid, CancellationToken, ValueTask<bool>> begin, Func<CancellationToken, ValueTask<bool>> authorizeDelete, CancellationToken ct = default);
}
public sealed record TenantBrowserProfileCommit(TenantAgent Agent, long SecurityRevision);
public interface ITenantBrowserProfileStore
{
    // Existing Instance/security/audit transaction, with actual current human and compound IAM checks.
    ValueTask<TenantBrowserProfileCommit?> TryBeginTenantProfileResetAsync(TenantBrowserAgentOwner owner, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, AgentBrowserProfileReset reset, AuthorizationAudit audit, CancellationToken ct = default);
    // Facts only; no fresh mutation authority or re-dispatch. Match the exact pending Instance-owned operation.
    ValueTask<TenantBrowserProfileCommit?> TrySettleTenantProfileResetAsync(TenantBrowserAgentOwner owner, Guid operationId,
        string status, string reasonCode, DateTimeOffset at, Guid auditId, CancellationToken ct = default);
}
