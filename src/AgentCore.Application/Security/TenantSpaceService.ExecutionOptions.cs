using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record TenantExecutionContextOptions(IReadOnlyList<SessionSpaceOption> Spaces, int Page, bool HasMore,
    Guid? SpaceId, bool TargetAvailable, bool EnvironmentsEnabled, bool EnvironmentSelectionAllowed,
    IReadOnlyList<SessionEnvironmentOption> Environments, long SecurityRevision);

public sealed partial class TenantSpaceService
{
    public async ValueTask<TenantExecutionContextOptions> ExecutionOptionsAsync(Guid actor, Guid tenant, Guid instance, Guid? destination,
        int page = 0, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || destination == Guid.Empty) throw new ArgumentException("Invalid context page or Space.");
        var state = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var raw = await spaces.ListSpacesAsync(tenant, null, page, 50, ct).ConfigureAwait(false); var choices = new List<SessionSpaceOption>();
        foreach (var candidate in raw.Items)
            if (await Readable(candidate).ConfigureAwait(false)) choices.Add(new(candidate.SpaceId, candidate.Name, candidate.Description, candidate.Kind.ToString(), candidate.EnvironmentsEnabled));
        var target = destination is { } selected ? await spaces.FindSpaceAsync(tenant, selected, ct).ConfigureAwait(false) : null;
        if (target is not null && !await Readable(target).ConfigureAwait(false)) target = null;
        var environmentAllowed = target?.EnvironmentsEnabled == true && environmentChoices is not null;
        if (environmentAllowed)
            foreach (var principal in new[] { actor, agent.PrincipalId })
                foreach (var key in new[] { "environments.read", "environments.activate" })
                    environmentAllowed &= (await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get(key), Scope(tenant, target!.SpaceId), ct).ConfigureAwait(false)).Allowed;
        var profiles = environmentAllowed
            ? (await environmentChoices!.ListEnvironmentsAsync(tenant, target!.SpaceId, ct).ConfigureAwait(false)).Select(e => new SessionEnvironmentOption(e.EnvironmentId, e.Name, e.Description, e.Revision)).ToArray() : [];
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        return new(choices, page, (long)(page + 1) * 50 < raw.Total, destination, target is not null, target?.EnvironmentsEnabled == true,
            environmentAllowed, profiles, state.Tenant.Revision);

        async ValueTask<bool> Readable(Space candidate)
        {
            if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
                || candidate.Kind == SpaceKind.Private && candidate.OwnerInstanceId != instance
                || !(await spaces.ReadMembershipsAsync(tenant, candidate.SpaceId, ct).ConfigureAwait(false)).Any(m => m.InstanceId == instance)) return false;
            foreach (var principal in new[] { actor, agent.PrincipalId })
                if (!(await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get("spaces.read"), Scope(tenant, candidate.SpaceId), ct).ConfigureAwait(false)).Allowed) return false;
            return true;
        }
    }
}
