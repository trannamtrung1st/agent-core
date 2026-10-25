using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record SessionSpaceOption(Guid SpaceId, string Name, string Description, string Kind, bool EnvironmentsEnabled);
public sealed record SessionEnvironmentOption(Guid EnvironmentId, string Name, string Description, long Revision);
public sealed record TenantContextOptions(IReadOnlyList<SessionSpaceOption> Spaces, int Page, bool HasMore,
    Guid SpaceId, bool EnvironmentsEnabled, bool EnvironmentSelectionAllowed, bool CanSwitch, bool CanClearEnvironment,
    IReadOnlyList<SessionEnvironmentOption> Environments, long SecurityRevision, long SessionRevision, long SelectionRevision);

public sealed partial class TenantSessionContextService
{
    public async ValueTask<TenantContextOptions> OptionsAsync(Guid tenant, Guid session, TenantSessionInitiator human,
        Guid? destination = null, int page = 0, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || destination == Guid.Empty) throw new ArgumentException("Invalid context page or Space.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await iam.RequireAsync(human.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, session), ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var selection = saved.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required");
        var raw = await spaces.ListSpacesAsync(tenant, null, page, 50, ct).ConfigureAwait(false); var options = new List<SessionSpaceOption>();
        foreach (var space in raw.Items)
            if (await Readable(space).ConfigureAwait(false)) options.Add(new(space.SpaceId, space.Name, space.Description, space.Kind.ToString(), space.EnvironmentsEnabled));
        var wanted = destination ?? selection.SpaceId;
        var target = await spaces.FindSpaceAsync(tenant, wanted, ct).ConfigureAwait(false);
        if (target is not null && !await Readable(target).ConfigureAwait(false)) target = null;
        // An explicitly requested foreign/unavailable target does not disclose its metadata.
        if (destination is not null && target is null) throw new AccessDeniedException("space_unavailable");
        var canSwitch = saved.Snapshot.ArchivedAt is null && saved.Snapshot.DurablyDeletedAt is null
            && saved.Snapshot.LifecycleStatus == SessionLifecycleStatus.Active && saved.Snapshot.Status is not (SessionStatus.Ended or SessionStatus.Ending)
            && agent.Instance.Lifecycle == AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
            && (await iam.CheckAsync(human.PrincipalId, "sessions.manage", TenantSessionRules.Scope(tenant, session), ct).ConfigureAwait(false)).Allowed;
        var environmentAllowed = target?.EnvironmentsEnabled == true && canSwitch;
        if (environmentAllowed)
            foreach (var actor in new[] { human.PrincipalId, agent.PrincipalId })
                foreach (var key in new[] { "environments.read", "environments.activate" })
                    environmentAllowed &= (await iam.CheckAsync(actor, key, TenantSpaceService.Scope(tenant, wanted), ct).ConfigureAwait(false)).Allowed;
        var profiles = environmentAllowed
            ? (await environments.ListEnvironmentsAsync(tenant, wanted, ct).ConfigureAwait(false)).Select(e => new SessionEnvironmentOption(e.EnvironmentId, e.Name, e.Description, e.Revision)).ToArray() : [];
        var current = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision
            || current is null || current.Snapshot.ContextSelection != selection || current.Snapshot.LifecycleStatus != saved.Snapshot.LifecycleStatus
            || current.Snapshot.ArchivedAt != saved.Snapshot.ArchivedAt || current.Snapshot.DurablyDeletedAt != saved.Snapshot.DurablyDeletedAt)
            throw new SecurityConflictException("stale_revision");
        return new(options, page, (long)(page + 1) * 50 < raw.Total, wanted, target?.EnvironmentsEnabled == true, environmentAllowed, canSwitch,
            canSwitch && target is not null && wanted == selection.SpaceId && selection.EnvironmentId is not null,
            profiles, state.Tenant.Revision, current.Snapshot.Revision, selection.Revision);

        async ValueTask<bool> Readable(Space space)
        {
            if (space.Kind == SpaceKind.Private && space.OwnerInstanceId != agent.Instance.InstanceId || agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
                || !(await spaces.ReadMembershipsAsync(tenant, space.SpaceId, ct).ConfigureAwait(false)).Any(m => m.InstanceId == agent.Instance.InstanceId)) return false;
            foreach (var actor in new[] { human.PrincipalId, agent.PrincipalId })
                if (!(await iam.CheckAsync(actor, "spaces.read", TenantSpaceService.Scope(tenant, space.SpaceId), ct).ConfigureAwait(false)).Allowed) return false;
            return true;
        }
    }
}
