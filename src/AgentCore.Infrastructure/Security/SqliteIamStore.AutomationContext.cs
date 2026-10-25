using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async ValueTask RequireAutomationContextAuthoring(AgentCoreDbContext db, TenantSecurityState state, TenantAgent agent,
        Automation? current, Automation proposed, Guid? human, IamAuthorizer iam, CancellationToken ct, bool force = false)
    {
        if (!force && !TenantAutomationContextRules.CheckAuthoring(current, proposed)) return;
        var tenant = state.Tenant.TenantId; var instance = agent.Instance.InstanceId.ToString("D");
        var own = await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.OwnerInstanceId == instance && s.DeletedAt == null, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("private_space_unavailable");
        var id = proposed.PinnedSpaceId ?? own.SpaceId;
        var active = await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SpaceId == id && s.DeletedAt == null, ct).ConfigureAwait(false);
        var membership = await db.Set<SpaceMembershipRecord>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenant && m.SpaceId == id && m.InstanceId == instance, ct).ConfigureAwait(false);
        var environment = proposed.PinnedEnvironmentId is { } profile ? await db.Set<SpaceEnvironmentRecord>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenant && e.SpaceId == id && e.EnvironmentId == profile && e.DeletedAt == null, ct).ConfigureAwait(false) : null;
        var root = proposed.ExecutionTarget.SessionId is { } session ? await OwnedSessions(db, tenant).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == session.ToString("D"), ct).ConfigureAwait(false) : null;
        TenantAutomationContextRules.Require(proposed, agent, own.Domain(), active?.Domain(), membership?.Domain([], []), environment?.Domain(), root is null ? null : await SessionView(db, root, ct).ConfigureAwait(false));
        await TenantAutomationContextRules.PermissionsAsync(state, agent.PrincipalId, id, environment is not null, human, iam, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireAutomationContextUnavailable(AgentCoreDbContext db, TenantSecurityState state, TenantAgent agent, Automation current, CancellationToken ct)
    {
        try { await RequireAutomationContextAuthoring(db, state, agent, current, current, null, new IamAuthorizer(this), ct, force: true).ConfigureAwait(false); }
        catch (AccessDeniedException error) when (TenantAutomationContextRules.Failure(error.ReasonCode) is not null) { return; }
        throw new SecurityConflictException("automation_context_available");
    }
}
