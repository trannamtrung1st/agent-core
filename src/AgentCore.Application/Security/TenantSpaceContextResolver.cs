using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

/// <summary>The one Space/Membership/profile source resolver used by admission and current protected boundaries.</summary>
public sealed class TenantSpaceContextResolver(ITenantSpaceStore spaces, ITenantEnvironmentStore environments, ITenantAgentStore agents, IamAuthorizer authorizer)
{
    public async ValueTask<(Space Space, SpaceMembership Membership, AgentCore.Domain.Spaces.Environment? Environment, Space Private)> CurrentAsync(
        Guid tenant, Guid instance, Guid spaceId, Guid? environmentId, long environmentSelectionEpoch, CancellationToken ct)
    {
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        var space = await spaces.FindSpaceAsync(tenant, spaceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        var membership = (await spaces.ReadMembershipsAsync(tenant, spaceId, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == instance)
            ?? throw new AccessDeniedException("space_membership_unavailable");
        if (space.Kind == SpaceKind.Private && space.OwnerInstanceId != instance) throw new AccessDeniedException("space_unavailable");
        var own = await spaces.FindPrivateSpaceAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("private_space_unavailable");
        await authorizer.RequireAsync(agent.PrincipalId, "spaces.read", TenantSpaceService.Scope(tenant, spaceId), ct).ConfigureAwait(false);
        AgentCore.Domain.Spaces.Environment? environment = null;
        if (environmentId is { } id)
        {
            if (!space.EnvironmentsEnabled) throw new AccessDeniedException("environments_disabled");
            if (space.EnvironmentSelectionEpoch != environmentSelectionEpoch) throw new AccessDeniedException("environment_selection_expired");
            environment = await environments.FindEnvironmentAsync(tenant, spaceId, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("environment_unavailable");
            await authorizer.RequireAsync(agent.PrincipalId, "environments.read", TenantSpaceService.Scope(tenant, spaceId), ct).ConfigureAwait(false);
        }
        return (space, membership, environment, own);
    }
    public async ValueTask<RunSpaceContext> CaptureAsync(Guid tenant, Guid instance, SessionContextSelection selection, CancellationToken ct)
    {
        selection.Validate(); if (selection.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        var (space, membership, environment, own) = await CurrentAsync(tenant, instance, selection.SpaceId, selection.EnvironmentId, selection.EnvironmentSelectionEpoch, ct).ConfigureAwait(false);
        var captured = new RunSpaceContext(tenant, space.SpaceId, own.SpaceId, environment?.EnvironmentId, space.Revision, membership.Revision, environment?.Revision,
            environment is null ? 0 : space.EnvironmentSelectionEpoch, space.Name, space.Instructions, membership.Responsibility, membership.Instructions,
            space.EnvironmentsEnabled, environment?.Name, selection.Revision);
        captured.Validate(); return captured;
    }
    public async ValueTask RequireCurrentAsync(AgentRun run, CancellationToken ct)
    {
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required"); pin.Validate();
        if (pin.TenantId != run.Owner.TenantId) throw new AccessDeniedException("tenant_context_mismatch");
        var current = await CurrentAsync(pin.TenantId, run.AgentInstanceId, pin.SpaceId, pin.EnvironmentId, pin.EnvironmentSelectionEpoch, ct).ConfigureAwait(false);
        if (current.Private.SpaceId != pin.PrivateSpaceId) throw new AccessDeniedException("private_space_changed");
        if (run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } human)
        {
            await authorizer.RequireAsync(human, "spaces.read", TenantSpaceService.Scope(pin.TenantId, pin.SpaceId), ct).ConfigureAwait(false);
            if (pin.EnvironmentId is not null) await authorizer.RequireAsync(human, "environments.read", TenantSpaceService.Scope(pin.TenantId, pin.SpaceId), ct).ConfigureAwait(false);
        }
    }
    // Owning stores have resolved this exact Space in their transaction; keep IAM evaluation
    // inside the incumbent engine without opening a second resource-ownership transaction.
    public static async ValueTask RequireCurrentPermissionsAsync(TenantSecurityState state, Guid principal, Guid space, bool environmentSelected,
        IamAuthorizer iam, CancellationToken ct, Guid? requester = null)
    {
        if (space == Guid.Empty) throw new AccessDeniedException("space_unavailable");
        foreach (var actor in requester is { } human ? new[] { principal, human }.Distinct() : new[] { principal })
        foreach (var key in environmentSelected ? new[] { "spaces.read", "environments.read" } : new[] { "spaces.read" })
        {
            var decision = await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), TenantSpaceService.Scope(state.Tenant.TenantId, space), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }

}
