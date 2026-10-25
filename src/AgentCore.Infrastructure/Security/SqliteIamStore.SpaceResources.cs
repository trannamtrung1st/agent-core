using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<SpaceFileResource?> FindSpaceResourceAsync(Guid tenant, Guid space, Guid resource, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Set<SpaceRecord>().AnyAsync(s => s.TenantId == tenant && s.SpaceId == space && s.DeletedAt == null, ct).ConfigureAwait(false)) return null;
        return await db.Set<SpaceFileResource>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.SpaceId == space && r.ResourceId == resource && r.DeletedAt == null, ct).ConfigureAwait(false);
    }
    public async ValueTask<SpaceResourcePage> ReadSpaceResourcesAsync(Guid tenant, Guid space, int page, int pageSize, CancellationToken ct = default)
    {
        SpaceStoreRules.Page(tenant, page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Set<SpaceRecord>().AnyAsync(s => s.TenantId == tenant && s.SpaceId == space && s.DeletedAt == null, ct).ConfigureAwait(false)) return new([], 0, page, pageSize);
        var query = db.Set<SpaceFileResource>().AsNoTracking().Where(r => r.TenantId == tenant && r.SpaceId == space && r.DeletedAt == null);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderBy(r => r.ResourceId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows, total, page, pageSize);
    }
    public async ValueTask<bool> TrySaveSpaceResourceAsync(TenantSecurityState next, long expectedSecurityRevision, long expectedSpaceRevision,
        SpaceFileResource value, long expectedResourceRevision, AuthorizationAudit audit, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); SpaceResourceStoreRules.Validate(next, expectedSecurityRevision, expectedSpaceRevision, value, expectedResourceRevision, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == value.TenantId, ct).ConfigureAwait(false);
        var owner = await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == value.TenantId && s.SpaceId == value.SpaceId && s.DeletedAt == null, ct).ConfigureAwait(false);
        if (tenant?.Revision != expectedSecurityRevision || owner?.Revision != expectedSpaceRevision) return false;
        var before = (await ReadTenantAsync(db, value.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        await RequireAgentHumanAsync(db, value.TenantId, audit.ActorPrincipalId, human, ct).ConfigureAwait(false);
        var current = await db.Set<SpaceFileResource>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.SpaceId == value.SpaceId && r.ResourceId == value.ResourceId, ct).ConfigureAwait(false);
        if (!SpaceResourceStoreRules.Matches(current, value, expectedResourceRevision)) return false;
        if (value.DeletedAt is not null && await db.Set<SpaceAddedBindingRecord>().AnyAsync(b => b.TenantId == value.TenantId && b.SpaceId == value.SpaceId
            && b.Kind == SpaceBindingKind.SpaceFile && b.ResourceKey == value.Key, ct).ConfigureAwait(false)) throw new SecurityConflictException("space_resource_in_use");
        SpaceResourceStoreRules.Limits(await db.Set<SpaceFileResource>().AsNoTracking().Where(r => r.TenantId == value.TenantId && r.SpaceId == value.SpaceId && r.DeletedAt == null).ToArrayAsync(ct).ConfigureAwait(false), value);
        if (current is null) db.Add(value); else db.Entry(current).CurrentValues.SetValues(value);
        db.Entry(owner!).CurrentValues.SetValues(owner! with { Revision = checked(owner!.Revision + 1), UpdatedAt = value.UpdatedAt });
        db.Entry(tenant!).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException exception) when (IsConstraint(exception)) { return false; }
    }
}
