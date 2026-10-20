using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWebhookStore
{
    public async ValueTask<WebhookEvent?> FindTenantWebhookAsync(Guid tenant, Guid source, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.ResourceId == source.ToString("D"), ct);
        return row is null ? null : SqliteExternalEventStore.ToSource(row);
    }
    public async ValueTask<TenantWebhookPage> ListTenantWebhooksAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); var ids = allowed?.Select(id => id.ToString("D")).ToArray();
        var query = db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenant && (ids == null || ids.Contains(s.ResourceId)));
        var total = await query.CountAsync(ct); var rows = await query.OrderBy(s => s.ResourceId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(SqliteExternalEventStore.ToSource).ToArray(), total, page, pageSize);
    }
    public async ValueTask<TenantWebhookReceiptPage> ListTenantWebhookReceiptsAsync(Guid tenant, Guid source, int page, int pageSize, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); var id = source.ToString("D");
        var query = db.ExternalEvents.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenant && r.ResourceId == id
            && db.WebhookEvents.IgnoreQueryFilters().Any(s => s.TenantId == tenant && s.ResourceId == r.ResourceId));
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(r => r.AdmittedAtUtc).ThenBy(r => r.EventId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(SqliteExternalEventStore.ToEvent).ToArray(), total, page, pageSize);
    }
    public async ValueTask<bool> TrySaveTenantWebhookAsync(Guid tenant, long expectedSecurityRevision, WebhookEvent source,
        long expectedSourceRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "webhooks.manage" || audit.Target != source.ResourceId.ToString("D")
            || audit.Decision != "allowed" || audit.At != source.UpdatedAtUtc) throw new ArgumentException("Invalid webhook management transaction.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenantRow = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == tenant, ct);
        if (tenantRow?.Revision != expectedSecurityRevision) return false;
        await RequireHumanSessionAsync(db, tenant, human, ct);
        var row = await db.WebhookEvents.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.TenantId == tenant && s.ResourceId == source.ResourceId.ToString("D"), ct);
        if ((row?.Revision ?? 0) != expectedSourceRevision) return false;
        TenantWebhookRules.Source(row is null ? null : SqliteExternalEventStore.ToSource(row), source, tenant, human, expectedSourceRevision);
        var state = (await ReadTenantAsync(db, tenant, ct))!;
        await TenantWebhookRules.HumanAsync(state, human.PrincipalId, source.ResourceId, row is null, new IamAuthorizer(this), ct);
        if (await db.WebhookEvents.IgnoreQueryFilters().AnyAsync(s => s.TenantId == tenant && s.EventKey == source.EventKey && s.ResourceId != source.ResourceId.ToString("D"), ct))
            throw new SecurityConflictException("webhook_key_conflict");
        if (row is null) db.WebhookEvents.Add(SqliteExternalEventStore.ToRecord(source)); else SqliteExternalEventStore.Copy(row, source);
        // Only this source, its audit and the current tenant revision may change.
        // A source command never accepts or rewrites a caller IAM child graph.
        db.Entry(tenantRow!).CurrentValues.SetValues(tenantRow! with { Revision = checked(expectedSecurityRevision + 1) }); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { throw new SecurityConflictException("webhook_write_conflict"); }
    }

    public async ValueTask<TenantWebhookAdmission> TryAdmitTenantWebhookAsync(Guid tenant, Guid source, string token, ExternalEvent receipt, CancellationToken ct = default)
    {
        TenantWebhookRules.Receipt(tenant, source, receipt); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.ResourceId == source.ToString("D"), ct);
        if (current?.Status != (int)WebhookEventStatus.Active || current.Revision != receipt.SourceRevision || !WebhookTokens.Matches(current.CredentialHash, token)
            || !await db.Set<Tenant>().AnyAsync(t => t.TenantId == tenant && t.Status == IdentityStatus.Active, ct)) return new(TenantWebhookAdmissionKind.Unauthorized);
        var prior = await db.ExternalEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ResourceId == source.ToString("D") && r.SourceEventId == receipt.SourceEventId, ct);
        if (prior is not null)
        {
            var same = TenantWebhookRules.SameReceipt(SqliteExternalEventStore.ToEvent(prior), receipt);
            return new(same ? TenantWebhookAdmissionKind.Duplicate : TenantWebhookAdmissionKind.Conflict, same ? Guid.Parse(prior.EventId) : null);
        }
        db.ExternalEvents.Add(SqliteExternalEventStore.ToRecord(TenantWebhookRules.Record(receipt, ApprovalNow)));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(TenantWebhookAdmissionKind.Accepted, receipt.EventId); }
        catch (DbUpdateException error) when (IsConstraint(error)) { throw new SecurityConflictException("webhook_admission_conflict"); }
    }
}
