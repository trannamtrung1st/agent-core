using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<Space?> FindSpaceAsync(Guid tenant, Guid space, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SpaceId == space && s.DeletedAt == null, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<Space?> FindPrivateSpaceAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var id = instance.ToString("D");
        return (await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.OwnerInstanceId == id && s.DeletedAt == null, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<SpacePage> ListSpacesAsync(Guid tenant, IReadOnlyList<Guid>? allowedSpaces, int page, int pageSize, CancellationToken ct = default)
    {
        SpaceStoreRules.Page(tenant, page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<SpaceRecord>().AsNoTracking().Where(s => s.TenantId == tenant && s.DeletedAt == null);
        if (allowedSpaces is not null) query = query.Where(s => allowedSpaces.Contains(s.SpaceId));
        var count = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderBy(s => s.SpaceId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Select(s => s.Domain()).ToArray(), count, page, pageSize);
    }
    public async ValueTask<IReadOnlyList<SpaceMembership>> ReadMembershipsAsync(Guid tenant, Guid space, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<SpaceMembershipRecord>().AsNoTracking().Where(m => m.TenantId == tenant && m.SpaceId == space).OrderBy(m => m.InstanceId).ToArrayAsync(ct).ConfigureAwait(false);
        var disabled = await db.Set<SpaceDisabledBindingRecord>().AsNoTracking().Where(m => m.TenantId == tenant && m.SpaceId == space).ToArrayAsync(ct).ConfigureAwait(false);
        var added = await db.Set<SpaceAddedBindingRecord>().AsNoTracking().Where(m => m.TenantId == tenant && m.SpaceId == space).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(r => r.Domain(disabled.Where(b => b.InstanceId == r.InstanceId), added.Where(b => b.InstanceId == r.InstanceId))).ToArray();
    }
    public async ValueTask<bool> TryChangeSpaceAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceChange change,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); SpaceStoreRules.Validate(next, expectedSecurityRevision, change, audit);
        var space = change.Space; var member = change.Membership?.Freeze();
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == space.TenantId, ct).ConfigureAwait(false);
        if (tenant?.Revision != expectedSecurityRevision) return false;
        var before = (await ReadTenantAsync(db, space.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        await RequireAgentHumanAsync(db, space.TenantId, audit.ActorPrincipalId, initiator, ct).ConfigureAwait(false);
        var current = await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == space.TenantId && s.SpaceId == space.SpaceId, ct).ConfigureAwait(false);
        if (!SpaceStoreRules.Matches(current?.Domain(), change)) return false;
        SpaceMembershipRecord? existing = null;
        if (member is not null)
        {
            var id = member.InstanceId.ToString("D");
            existing = await db.Set<SpaceMembershipRecord>().SingleOrDefaultAsync(m => m.TenantId == space.TenantId && m.SpaceId == space.SpaceId && m.InstanceId == id, ct).ConfigureAwait(false);
            if (change.RemoveMembership && existing is null || (existing?.Revision ?? 0) != change.ExpectedMembershipRevision || !before.SystemIdentities.Any(i => i.AgentInstanceId == member.InstanceId && i.Status == IdentityStatus.Active)) return false;
            if (space.Kind == SpaceKind.Private && (change.RemoveMembership || member.InstanceId != space.OwnerInstanceId || existing is null))
                throw new AccessDeniedException("private_membership_managed");
            if (change.RemoveMembership) await RequireSpaceMemberQuiescent(db, space.TenantId, space.SpaceId, member.InstanceId, ct).ConfigureAwait(false);
        }
        if (change.Delete)
        {
            if (await db.Set<SpaceMembershipRecord>().AnyAsync(m => m.TenantId == space.TenantId && m.SpaceId == space.SpaceId, ct).ConfigureAwait(false)) throw new SecurityConflictException("space_has_memberships");
            db.Entry(current!).CurrentValues.SetValues(current! with { DeletedAt = audit.At, UpdatedAt = audit.At, Revision = space.Revision });
            await db.Set<EnvironmentVariableRecord>().Where(v => v.TenantId == space.TenantId && v.SpaceId == space.SpaceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var profiles = await db.Set<SpaceEnvironmentRecord>().Where(e => e.TenantId == space.TenantId && e.SpaceId == space.SpaceId && e.DeletedAt == null).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var profile in profiles) db.Entry(profile).CurrentValues.SetValues(profile with { Revision = checked(profile.Revision + 1), DeletedAt = audit.At, UpdatedAt = audit.At });
        }
        else if (current is null) db.Add(SpaceRecord.Row(space)); else db.Entry(current).CurrentValues.SetValues(SpaceRecord.Row(space));
        if (member is not null)
        {
            var id = member.InstanceId.ToString("D");
            await db.Set<SpaceDisabledBindingRecord>().Where(m => m.TenantId == space.TenantId && m.SpaceId == space.SpaceId && m.InstanceId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Set<SpaceAddedBindingRecord>().Where(m => m.TenantId == space.TenantId && m.SpaceId == space.SpaceId && m.InstanceId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (change.RemoveMembership) { if (existing is not null) db.Remove(existing); }
            else
            {
                if (existing is null) db.Add(SpaceMembershipRecord.Row(member)); else db.Entry(existing).CurrentValues.SetValues(SpaceMembershipRecord.Row(member));
                db.AddRange(member.DisabledInheritedBindingKeys.Select(k => new SpaceDisabledBindingRecord(space.TenantId, space.SpaceId, id, k)));
                db.AddRange(member.AddedBindings.Select(b => new SpaceAddedBindingRecord(space.TenantId, space.SpaceId, id, b.Kind, b.ResourceKey, b.AccountRef)));
            }
        }
        db.Entry(tenant!).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    private static void ProvisionPrivateSpace(AgentCoreDbContext db, Guid tenant, AgentCore.Domain.Definitions.AgentInstance instance)
    {
        var at = instance.CreatedAt; var space = new Space(tenant, Guid.NewGuid(), SpaceKind.Private, instance.InstanceId, "Private Space", "", "", false, 1, at, at);
        space.Validate(); db.Add(SpaceRecord.Row(space));
        db.Add(SpaceMembershipRecord.Row(new(tenant, space.SpaceId, instance.InstanceId, "", "", [], [], 1, at, at)));
    }
}
