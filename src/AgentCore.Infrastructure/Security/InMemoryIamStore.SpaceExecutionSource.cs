using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private async ValueTask RequireSpaceExecutionSource(AgentRun run, TenantSecurityState state, IamAuthorizer iam, CancellationToken ct, bool admission = false)
    {
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required"); pin.Validate();
        var tenant = run.Owner.TenantId!.Value;
        if (pin.TenantId != tenant || state.Tenant.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        var space = FindSpaceAsync(tenant, pin.SpaceId, ct).Result ?? throw new AccessDeniedException("space_unavailable");
        var member = _spaceMemberships.GetValueOrDefault((tenant, pin.SpaceId, run.AgentInstanceId));
        if (space.Kind == AgentCore.Domain.Spaces.SpaceKind.Private && space.OwnerInstanceId != run.AgentInstanceId
            || member is null
            || FindPrivateSpaceAsync(tenant, run.AgentInstanceId, ct).Result?.SpaceId != pin.PrivateSpaceId) throw new AccessDeniedException("space_membership_unavailable");
        if (pin.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != pin.EnvironmentSelectionEpoch
            || FindEnvironmentAsync(tenant, pin.SpaceId, environment, ct).Result is null)) throw new AccessDeniedException("environment_unavailable");
        if (admission && (space.Revision != pin.SpaceRevision || member!.Revision != pin.MembershipRevision
            || pin.EnvironmentId is { } selected && FindEnvironmentAsync(tenant, pin.SpaceId, selected, ct).Result?.Revision != pin.EnvironmentRevision))
            throw new SecurityConflictException("space_context_changed");
        await TenantSpaceContextResolver.RequireCurrentPermissionsAsync(state, run.Owner.ExecutorPrincipalId!.Value, pin.SpaceId, pin.EnvironmentId is not null, iam, ct, run.Admission.DelegatedTask is null && run.Admission.AgentMessage is null ? run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId : null).ConfigureAwait(false);
    }
}
