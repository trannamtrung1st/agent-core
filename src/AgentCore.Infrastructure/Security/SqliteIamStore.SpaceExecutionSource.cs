using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async ValueTask RequireSpaceExecutionSource(AgentCoreDbContext db, AgentRun run, TenantSecurityState state, IamAuthorizer iam, CancellationToken ct, bool admission = false)
    {
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required"); pin.Validate();
        var tenant = run.Owner.TenantId!.Value; var instance = run.AgentInstanceId.ToString("D");
        if (pin.TenantId != tenant || state.Tenant.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        var space = await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SpaceId == pin.SpaceId && s.DeletedAt == null, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        var member = await db.Set<SpaceMembershipRecord>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenant && m.SpaceId == pin.SpaceId && m.InstanceId == instance, ct).ConfigureAwait(false);
        if (space.Kind == AgentCore.Domain.Spaces.SpaceKind.Private && space.OwnerInstanceId != instance
            || member is null
            || !await db.Set<SpaceRecord>().AnyAsync(s => s.TenantId == tenant && s.SpaceId == pin.PrivateSpaceId && s.OwnerInstanceId == instance && s.DeletedAt == null, ct).ConfigureAwait(false)) throw new AccessDeniedException("space_membership_unavailable");
        if (pin.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != pin.EnvironmentSelectionEpoch
            || !await db.Set<SpaceEnvironmentRecord>().AnyAsync(e => e.TenantId == tenant && e.SpaceId == pin.SpaceId && e.EnvironmentId == environment && e.DeletedAt == null, ct).ConfigureAwait(false))) throw new AccessDeniedException("environment_unavailable");
        if (admission && (space.Revision != pin.SpaceRevision || member!.Revision != pin.MembershipRevision
            || pin.EnvironmentId is { } selected && !await db.Set<SpaceEnvironmentRecord>().AnyAsync(e => e.TenantId == tenant && e.SpaceId == pin.SpaceId
                && e.EnvironmentId == selected && e.Revision == pin.EnvironmentRevision && e.DeletedAt == null, ct).ConfigureAwait(false)))
            throw new SecurityConflictException("space_context_changed");
        await TenantSpaceContextResolver.RequireCurrentPermissionsAsync(state, run.Owner.ExecutorPrincipalId!.Value, pin.SpaceId, pin.EnvironmentId is not null, iam, ct, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId).ConfigureAwait(false);
    }
}
