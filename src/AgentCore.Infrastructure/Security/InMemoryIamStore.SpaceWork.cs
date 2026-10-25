using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    // Called under the IAM and Session admission gates. The captured owner, rather than
    // the mutable next-turn selection, determines which root work can still require.
    private bool HasSpaceWork(Guid tenant, Guid space, Guid? instance = null) =>
        sessionBacking.AgentRuns.Runs.Values.Any(r => r.Owner.TenantId == tenant
            && (instance is null || r.AgentInstanceId == instance)
            && r.Admission.Configuration?.SpaceContext is { } context
            && (context.SpaceId == space || context.PrivateSpaceId == space)
            && (!r.IsTerminal || r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
        || _approvals.Values.Any(a => a.TenantId == tenant && (instance is null || a.Operation.InstanceId == instance)
            && a.Operation.ArtifactPublication is not null && a.Operation.SpaceContext is { } context
            && (context.SpaceId == space || context.PrivateSpaceId == space)
            && a.Status is TenantApprovalStatus.Pending or TenantApprovalStatus.Approved
            && a.ExpiresAt > (clock ?? TimeProvider.System).GetUtcNow() && !_operationReceipts.ContainsKey(a.Operation.OperationId));

    private void RequireSpaceMemberQuiescent(Guid tenant, Guid space, Guid instance)
    {
        if (HasSpaceWork(tenant, space, instance)) throw new SecurityConflictException("space_membership_has_active_work");
        if (sandboxBacking.HasUnretiredSpaceLease(tenant, space, instance)) throw new SecurityConflictException("space_membership_live_sandbox");
    }
}
