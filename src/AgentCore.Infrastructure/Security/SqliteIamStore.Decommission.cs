using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAgentDecommissionStore
{
    public async ValueTask<TenantAgentDecommissionSnapshot?> ReadAgentDecommissionAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await OwnedAgents(db, tenant, true).SingleOrDefaultAsync(a => a.Instance.InstanceId == instance.ToString("D"), ct);
        if (row is null) return null;
        return new(Owned(row), row.Identity.Status, await TenantDeletionReferenceQueries.CountInstanceAsync(db, instance.ToString("D"), ct, tenant),
            await DecommissionReceipt(db, tenant, instance, ct));
    }
    private static Task<AuthorizationAudit?> DecommissionReceipt(AgentCoreDbContext db, Guid tenant, Guid instance, CancellationToken ct) =>
        db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.Action == TenantAgentDecommissionRules.Action && a.Target == instance.ToString("D"), ct);
    public async ValueTask<bool> TryDecommissionAgentAsync(Guid tenant, Guid instance, HumanSessionReference human, long expectedSecurityRevision,
        long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantAgentDecommissionRules.Validate(tenant, instance, human, audit);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireHumanSessionAsync(db, tenant, human, ct);
        var state = await ReadTenantAsync(db, tenant, ct); var key = instance.ToString("D");
        var row = await OwnedAgents(db, tenant, true).SingleOrDefaultAsync(a => a.Instance.InstanceId == key, ct);
        if (row is null || state is null || !await TenantAgentDecommissionRules.Allowed(state, human.PrincipalId, instance, new IamAuthorizer(this), true, ct)) return false;
        if (await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == audit.AuditId, ct) is { } receipt)
            return row.Identity.Status == IdentityStatus.Removed && receipt.TenantId == tenant && receipt.Action == TenantAgentDecommissionRules.Action
                && receipt.Target == key && receipt.ActorPrincipalId == human.PrincipalId && row.Instance.Revision == expectedInstanceRevision + 1;
        if (state.Tenant.Revision != expectedSecurityRevision || row.Instance.Revision != expectedInstanceRevision || row.Identity.Status == IdentityStatus.Removed) return false;
        if (row.Instance.Lifecycle != nameof(AgentInstanceLifecycle.Archived)) throw new SecurityConflictException("archive_required");
        if ((await TenantDeletionReferenceQueries.CountInstanceAsync(db, key, ct, tenant)).HasReferences) throw new SecurityConflictException("agent_in_use");
        if (SqliteAgentInstanceStore.Map(row.Instance).BrowserProfileReset?.Status == "pending") throw new SecurityConflictException("browser_profile_busy");
        var privateHome = await db.Set<SpaceRecord>().SingleAsync(s => s.TenantId == tenant && s.OwnerInstanceId == key && s.DeletedAt == null, ct);
        var next = TenantAgentDecommissionRules.Retire(state, instance, row.Identity.PrincipalId, privateHome.SpaceId);
        db.Attach(row.Instance); row.Instance.Revision++; row.Instance.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
        await db.AgentCredentialBindings.Where(b => b.AgentInstanceId == key && b.TenantId == tenant).ExecuteDeleteAsync(ct);
        await db.AgentInstanceSkills.Where(s => s.AgentInstanceId == key).ExecuteDeleteAsync(ct);
        await db.AgentDefinitionSkillStates.Where(s => s.AgentInstanceId == key).ExecuteDeleteAsync(ct);
        await db.AgentInstanceResources.Where(s => s.InstanceId == key).ExecuteDeleteAsync(ct);
        await db.AgentDefinitionResourceStates.Where(s => s.InstanceId == key).ExecuteDeleteAsync(ct);
        var root = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct); db.Entry(root).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct); db.Add(audit);
        await RequireHumanSessionAsync(db, tenant, human, ct);
        try {
            await db.SaveChangesAsync(ct);
            var memberships = await db.Set<SpaceMembershipRecord>().Where(m => m.TenantId == tenant && m.InstanceId == key).Select(m => m.SpaceId).ToArrayAsync(ct);
            var spaces = await db.Set<SpaceRecord>().Where(s => s.TenantId == tenant && memberships.Contains(s.SpaceId)).ToArrayAsync(ct);
            foreach (var space in spaces)
                db.Entry(space).CurrentValues.SetValues(space with { Revision = checked(space.Revision + 1), UpdatedAt = audit.At,
                    DeletedAt = space.SpaceId == privateHome.SpaceId ? audit.At : space.DeletedAt,
                    EnvironmentsEnabled = space.SpaceId == privateHome.SpaceId ? false : space.EnvironmentsEnabled,
                    EnvironmentSelectionEpoch = space.SpaceId == privateHome.SpaceId && space.EnvironmentsEnabled ? checked(space.EnvironmentSelectionEpoch + 1) : space.EnvironmentSelectionEpoch });
            await db.Set<SpaceMembershipRecord>().Where(m => m.TenantId == tenant && m.InstanceId == key).ExecuteDeleteAsync(ct);
            await db.Set<EnvironmentVariableRecord>().Where(v => v.TenantId == tenant && v.SpaceId == privateHome.SpaceId).ExecuteDeleteAsync(ct);
            var profiles = await db.Set<SpaceEnvironmentRecord>().Where(e => e.TenantId == tenant && e.SpaceId == privateHome.SpaceId && e.DeletedAt == null).ToArrayAsync(ct);
            foreach (var profile in profiles) db.Entry(profile).CurrentValues.SetValues(profile with { Revision = checked(profile.Revision + 1), UpdatedAt = audit.At, DeletedAt = audit.At });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
    private static IQueryable<AgentJoin> DecommissionedAgents(AgentCoreDbContext db) =>
        from identity in db.Set<IamSystemIdentityRow>().AsNoTracking()
        join instance in db.AgentInstances.AsNoTracking() on identity.AgentInstanceId equals instance.InstanceId
        where identity.TenantId != null && identity.Status == IdentityStatus.Removed && identity.Kind == SystemIdentityKind.Agent
            && instance.Lifecycle == nameof(AgentInstanceLifecycle.Archived)
            && db.Set<Principal>().Any(p => p.PrincipalId == identity.PrincipalId && p.Status == IdentityStatus.Removed)
            && db.Set<AuthorizationAudit>().Any(a => a.TenantId == identity.TenantId && a.Action == TenantAgentDecommissionRules.Action && a.Target == instance.InstanceId && a.ReasonCode == "agent_decommissioned")
        select new AgentJoin { Identity = identity, Instance = instance };
    public async ValueTask<TenantAgentDecommissionPage> ListAgentDecommissionCleanupAsync(Guid? afterInstance, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct); var query = DecommissionedAgents(db);
        if (afterInstance is { } after) query = query.Where(a => string.Compare(a.Instance.InstanceId, after.ToString("D")) > 0);
        var all = await query.OrderBy(a => a.Instance.InstanceId).Take(limit + 1).ToArrayAsync(ct);
        return new(all.Take(limit).Select(a => new TenantBrowserAgentOwner(a.Identity.TenantId!.Value, Guid.Parse(a.Instance.InstanceId), a.Identity.PrincipalId)).ToArray(),
            all.Length > limit ? Guid.Parse(all[limit - 1].Instance.InstanceId) : null);
    }
    public async ValueTask<TenantWorkspaceReference.SpaceHome?> ReadDecommissionedPrivateWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await DecommissionedAgents(db).AnyAsync(a => a.Identity.TenantId == owner.TenantId && a.Identity.PrincipalId == owner.ExecutorPrincipalId && a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct)) return null;
        var key = owner.AgentInstanceId.ToString("D");
        var space = await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == owner.TenantId && s.OwnerInstanceId == key && s.DeletedAt != null, ct);
        return space is null ? null : new(owner.TenantId, space.SpaceId);
    }
    public async ValueTask<bool> CanPurgeDecommissionedAgentWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await contexts.CreateDbContextAsync(ct);
        return await DecommissionedAgents(db).AnyAsync(a => a.Identity.TenantId == owner.TenantId && a.Identity.PrincipalId == owner.ExecutorPrincipalId && a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct)
            && !await db.Set<SandboxLeaseRow>().AnyAsync(l => l.AgentInstanceId == owner.AgentInstanceId.ToString("D") && l.State != (int)AgentCore.Domain.Conversation.SandboxLeaseState.Terminated, ct);
    }
    public async ValueTask<bool> IsAgentDecommissionedAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); await using var db = await contexts.CreateDbContextAsync(ct);
        return await DecommissionedAgents(db).AnyAsync(a => a.Identity.TenantId == owner.TenantId && a.Identity.PrincipalId == owner.ExecutorPrincipalId && a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct);
    }
}
