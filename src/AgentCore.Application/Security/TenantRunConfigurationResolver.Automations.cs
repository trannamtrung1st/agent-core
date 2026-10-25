using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Spaces;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunConfigurationResolver
{
    // Saved pins are independent of completion delivery. An existing conversation must agree;
    // admission never changes its selection or adopts an unrelated context implicitly.
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> ResolveForAutomationAsync(
        Automation automation, TenantSession? conversation, DateTimeOffset at, CancellationToken ct)
    {
        var tenant = automation.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var own = await ((ITenantSpaceStore)agents).FindPrivateSpaceAsync(tenant, automation.Owner.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("private_space_unavailable");
        var selectedSpace = automation.PinnedSpaceId ?? own.SpaceId;
        var space = await ((ITenantSpaceStore)agents).FindSpaceAsync(tenant, selectedSpace, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("space_unavailable");
        var epoch = automation.PinnedEnvironmentId is null ? 0 : space.EnvironmentSelectionEpoch;
        var selected = conversation?.Snapshot.ContextSelection ?? new SessionContextSelection(tenant, selectedSpace, automation.PinnedEnvironmentId, epoch, 1, at);
        if (selected.TenantId != tenant || selected.SpaceId != selectedSpace || selected.EnvironmentId != automation.PinnedEnvironmentId
            || selected.EnvironmentSelectionEpoch != epoch)
            throw new AccessDeniedException("automation_context_mismatch");
        try { await SpaceContexts.CurrentAsync(tenant, automation.Owner.AgentInstanceId, selectedSpace, automation.PinnedEnvironmentId, epoch, ct).ConfigureAwait(false); }
        catch (AccessDeniedException error) when (error.ReasonCode == "permission_denied") { throw new AccessDeniedException("automation_context_permission_required"); }
        return await ResolveInSpaceAsync(tenant, automation.Owner.AgentInstanceId, selected, ct).ConfigureAwait(false);
    }
}
