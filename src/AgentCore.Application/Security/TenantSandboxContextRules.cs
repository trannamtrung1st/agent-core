using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantSandboxContextRules
{
    public static void RequireRetired(SandboxLease? lease)
    {
        if (lease is not null && lease.State != SandboxLeaseState.Terminated)
            throw new SecurityConflictException("sandbox_context_retirement_required");
    }

    public static void RequireSelected(TenantSession session, AgentRun run)
    {
        var selected = session.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required");
        var captured = TenantWorkspaceRules.Context(run);
        if (selected.SpaceId != captured.SpaceId || selected.EnvironmentId != captured.EnvironmentId
            || selected.EnvironmentSelectionEpoch != captured.EnvironmentSelectionEpoch)
            throw new SecurityConflictException("sandbox_context_changed");
    }
}
