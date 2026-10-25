using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record TenantSessionContextView(SessionContextSelection Selection, string State, string? ReasonCode,
    Space? Space, AgentCore.Domain.Spaces.Environment? Environment, long SessionRevision, long SecurityRevision);

public sealed partial class TenantSessionContextService(IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs, ITenantAgentStore agents,
    ITenantSpaceStore spaces, ITenantEnvironmentStore environments, IamAuthorizer iam, IdentityService identity,
    ITenantSessionContextControl control, ITenantSandboxContextIsolation? sandboxIsolation = null)
{
    public async ValueTask<TenantSessionContextView> ReadAsync(Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await iam.RequireAsync(human.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, session), ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var selection = saved.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"); selection.Validate();
        var space = await spaces.FindSpaceAsync(tenant, selection.SpaceId, ct).ConfigureAwait(false);
        var membership = (await spaces.ReadMembershipsAsync(tenant, selection.SpaceId, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == saved.Owner.AgentInstanceId);
        var readable = (await iam.CheckAsync(human.PrincipalId, "spaces.read", TenantSpaceService.Scope(tenant, selection.SpaceId), ct).ConfigureAwait(false)).Allowed;
        string? reason = space is null || !readable ? "space_unavailable" : membership is null ? "space_membership_unavailable" : null;
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (reason is null && (agent is null || agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
            || space!.Kind == SpaceKind.Private && space.OwnerInstanceId != agent.Instance.InstanceId
            || !(await iam.CheckAsync(agent.PrincipalId, "spaces.read", TenantSpaceService.Scope(tenant, selection.SpaceId), ct).ConfigureAwait(false)).Allowed))
            reason = "executor_context_unavailable";
        AgentCore.Domain.Spaces.Environment? environment = null;
        if (selection.EnvironmentId is { } id && reason is null)
        {
            if (!(await iam.CheckAsync(human.PrincipalId, "environments.read", TenantSpaceService.Scope(tenant, selection.SpaceId), ct).ConfigureAwait(false)).Allowed
                || !(await iam.CheckAsync(agent!.PrincipalId, "environments.read", TenantSpaceService.Scope(tenant, selection.SpaceId), ct).ConfigureAwait(false)).Allowed)
                reason = "environment_unavailable";
            else if (!space!.EnvironmentsEnabled) reason = "environments_disabled";
            else if (space.EnvironmentSelectionEpoch != selection.EnvironmentSelectionEpoch) reason = "environment_selection_expired";
            else environment = await environments.FindEnvironmentAsync(tenant, selection.SpaceId, id, ct).ConfigureAwait(false);
            if (environment is null && reason is null) reason = "environment_unavailable";
        }
        var pending = selection.EffectiveAfterRunId is { } barrier && await runs.FindTenantRunAsync(tenant, barrier, ct).ConfigureAwait(false) is { IsTerminal: false };
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(selection, reason is not null ? "stale" : pending ? "pending_next_turn" : "applied", reason, readable ? space : null, environment, saved.Snapshot.Revision, state.Tenant.Revision);
    }
    public async ValueTask<TenantContextSwitchResult> SwitchAsync(Guid tenant, Guid session, TenantSessionInitiator human, Guid transition,
        Guid space, Guid? environment, long expectedSecurity, long expectedSession, long expectedSelection, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var destination = await spaces.FindSpaceAsync(tenant, space, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        var membership = (await spaces.ReadMembershipsAsync(tenant, space, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == agent.Instance.InstanceId);
        var profile = environment is { } id ? await environments.FindEnvironmentAsync(tenant, space, id, ct).ConfigureAwait(false) : null;
        var request = new TenantContextSwitch(saved.Owner, human, transition, space, environment, profile is null ? 0 : destination.EnvironmentSelectionEpoch,
            expectedSecurity, expectedSession, expectedSelection, agent.Instance.Revision);
        if (TenantSessionContextRules.IsReplay(request, saved.Snapshot.ContextSelection))
        {
            request = request with { ExpectedSecurityRevision = state.Tenant.Revision, ExpectedSessionRevision = saved.Snapshot.Revision,
                ExpectedSelectionRevision = saved.Snapshot.ContextSelection!.Revision };
            await TenantSessionContextRules.RequireAsync(request, saved, agent, state, iam, destination, membership, profile, live: false, ct).ConfigureAwait(false);
            var view = await ReadAsync(tenant, session, human, ct).ConfigureAwait(false);
            return new(view.Selection, view.SessionRevision, view.SecurityRevision, view.State);
        }
        await TenantSessionContextRules.RequireAsync(request, saved, agent, state, iam, destination, membership, profile, live: false, ct).ConfigureAwait(false);
        if (sandboxIsolation is not null) await sandboxIsolation.RetireIdleGenerationAsync(saved.Owner, ct).ConfigureAwait(false);
        return await control.SwitchContextAsync(request, ct).ConfigureAwait(false);
    }
}
