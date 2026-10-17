using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantRuntimeRules
{
    public static void Require(TenantRuntimeFrame frame, TenantSessionRoot root, AgentRun run, DateTimeOffset at)
    {
        if (frame.Owner.TenantId is null || frame.Owner.ProfileIdOrNull is not null || frame.Owner != run.Owner || frame.RunId != run.AgentRunId
            || frame.SessionId != run.SessionId || root.SessionId != frame.SessionId || root.TenantId != frame.Owner.TenantId
            || root.AgentInstanceId != frame.Owner.AgentInstanceId || root.ExecutorPrincipalId != frame.Owner.ExecutorPrincipalId
            || run.Admission.ExecutionContext is null || run.Status != AgentRunStatus.Running || run.Claim?.Generation != frame.Generation
            || run.CancellationRequested || run.Claim.LeaseExpiresAtUtc <= at)
            throw new AccessDeniedException("tenant_runtime_unavailable");
    }
    public static void ValidateSnapshot(TenantRuntimeFrame frame, SessionSnapshot before, SessionSnapshot next)
    {
        if (next.SessionId != frame.SessionId || next.AgentInstanceId != frame.Owner.AgentInstanceId || next.ProfileId is not null
            || before.Origin != next.Origin || before.Definition.Id != next.Definition.Id || before.Definition.Version != next.Definition.Version
            || next.DurablyDeletedAt is not null || next.ArchivedAt != before.ArchivedAt || before.DurablyDeletedAt is not null)
            throw new AccessDeniedException("tenant_runtime_scope_mismatch");
    }
    public static void RequireOwner(AgentRunOwner owner)
    {
        if (owner.TenantId is null || owner.ExecutorPrincipalId is null || owner.ProfileIdOrNull is not null) throw new AccessDeniedException("tenant_context_required");
    }
}
