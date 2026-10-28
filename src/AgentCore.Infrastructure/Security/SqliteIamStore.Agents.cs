using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = instanceId.ToString("D");
        var row = await OwnedAgents(db, tenantId).SingleOrDefaultAsync(x => x.Instance.InstanceId == id, ct).ConfigureAwait(false);
        return row is null ? null : Owned(row);
    }

    public async ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedAgents(db, tenantId);
        if (allowedInstanceIds is not null)
        {
            var allowed = allowedInstanceIds.Select(id => id.ToString("D")).ToArray();
            query = query.Where(x => allowed.Contains(x.Instance.InstanceId));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(x => x.Identity.ManagedKind != null).ThenBy(x => x.Instance.InstanceId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Select(Owned).ToArray(), total, page, pageSize);
    }

    public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, false, ct, initiator);

    public ValueTask<bool> TryProvisionGeneralAssistantAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, true, ct);

    private async ValueTask<bool> CreateTenantAgentCoreAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, bool managedProvision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        next = IamStateRules.Freeze(next); IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instance.InstanceId, instance.Lifecycle, audit);
        ManagedGeneralAssistant.ValidateProvision(next, instance, audit, managedProvision);
        if (instance.Revision != 1 || instance.Lifecycle != AgentInstanceLifecycle.Active || instance.BrowserProfileReset is not null) throw new ArgumentException("A new active Instance is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expectedSecurityRevision) return false;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        ManagedGeneralAssistant.ValidateMarkerAdditions(before, next, managedProvision ? instance.InstanceId : null);
        if (!managedProvision) await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct, instance.InstanceId).ConfigureAwait(false);
        db.AgentInstances.Add(SqliteAgentInstanceStore.Map(instance));
        ProvisionPrivateSpace(db, next.Tenant.TenantId, instance);
        if (managedProvision)
        {
            var owner = next.SystemIdentities.Single(i => i.AgentInstanceId == instance.InstanceId);
            var space = db.ChangeTracker.Entries<SpaceRecord>().Single(e => e.Entity.OwnerInstanceId == instance.InstanceId.ToString("D")).Entity.SpaceId;
            var role = next.Roles.Single(r => r.Key == ManagedGeneralAssistant.FilesRole);
            next = IamStateRules.Freeze(next with { Assignments = next.Assignments.Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                owner.PrincipalId, role.RoleId, TenantWorkspaceRules.Home(next.Tenant.TenantId, space), Guid.Empty, instance.CreatedAt)).Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                owner.PrincipalId, role.RoleId, AccessScope.ForResource(next.Tenant.TenantId, "space", space.ToString("D")), Guid.Empty, instance.CreatedAt)).ToArray() });
        }
        await SqliteAgentInstanceStore.InitializeSkillsAsync(db, instance.InstanceId, skills, instance.UpdatedAt, ct).ConfigureAwait(false);
        db.Entry(current).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, initiator, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }

    public async ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        next = IamStateRules.Freeze(next); IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, lifecycle, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        if (before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId && i.ManagedKind is not null)) throw new SecurityConflictException("system_managed_instance");
        await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var id = instanceId.ToString("D");
        if (!before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId)) return null;
        var row = await db.AgentInstances.SingleOrDefaultAsync(i => i.InstanceId == id, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedInstanceRevision) return null;
        var old = SqliteAgentInstanceStore.Map(row);
        row.Lifecycle = lifecycle.ToString(); row.UpdatedAtUtc = at.ToUnixTimeMilliseconds(); row.Revision++;
        db.Entry(current).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        var updated = SqliteAgentInstanceStore.Map(row);
        foreach (var e in CoreEventPersistence.Instance(old, updated, AgentCore.Domain.Triggers.TriggerOwner.ForTenant(next.Tenant.TenantId, instanceId, before.SystemIdentities.Single(i => i.AgentInstanceId == instanceId).PrincipalId)))
            await TenantCoreEventProduction.StageAsync(db, e, ct).ConfigureAwait(false);
        await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, initiator, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
        var identity = next.SystemIdentities.Single(i => i.AgentInstanceId == instanceId);
        return new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, updated, identity.ManagedKind);
    }

    private sealed class AgentJoin
    {
        public required IamSystemIdentityRow Identity { get; init; }
        public required AgentInstanceRecord Instance { get; init; }
    }
    private static IQueryable<AgentJoin> OwnedAgents(AgentCoreDbContext db, Guid tenantId, bool includeRemoved = false)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.");
        return from identity in db.Set<IamSystemIdentityRow>().AsNoTracking()
            join instance in db.AgentInstances.AsNoTracking() on identity.AgentInstanceId equals instance.InstanceId
            where identity.TenantId == tenantId && identity.Kind == SystemIdentityKind.Agent && (includeRemoved || identity.Status != IdentityStatus.Removed)
            select new AgentJoin { Identity = identity, Instance = instance };
    }
    private static TenantAgent Owned(AgentJoin row) => new(row.Identity.TenantId!.Value, row.Identity.SystemIdentityId, row.Identity.PrincipalId, SqliteAgentInstanceStore.Map(row.Instance), row.Identity.ManagedKind);
    private static async Task ValidateTenantActorAsync(AgentCoreDbContext db, TenantSecurityState before, Guid actorId, CancellationToken ct)
    {
        var actor = before.Principals.SingleOrDefault(p => p.PrincipalId == actorId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        if (actor.Kind == PrincipalKind.User && !await db.Set<Account>().AnyAsync(a => a.AccountId == actor.AccountId && a.Status == IdentityStatus.Active, ct).ConfigureAwait(false))
            throw new AccessDeniedException("account_unavailable");
    }
    private async Task RequireAgentHumanAsync(AgentCoreDbContext db, Guid tenant, Guid actor, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return;
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        try { await RequireHumanSessionAsync(db, tenant, initiator, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }

}
