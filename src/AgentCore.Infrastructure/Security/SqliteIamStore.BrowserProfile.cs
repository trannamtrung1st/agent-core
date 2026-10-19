using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantBrowserProfileStore
{
    public async ValueTask<TenantBrowserProfileCommit?> TryBeginTenantProfileResetAsync(TenantBrowserAgentOwner owner, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, AgentBrowserProfileReset reset, AuthorizationAudit audit, CancellationToken ct = default)
    {
        owner.Validate(); TenantBrowserProfileRules.Validate(reset);
        if (reset.Status != "pending" || reset.PrincipalId != human.PrincipalId || audit.AuditId != reset.OperationId || audit.TenantId != owner.TenantId
            || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "browser.profile.reset" || audit.Target != owner.AgentInstanceId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != reset.ReasonCode || audit.At != reset.RequestedAt || audit.Execution is not null)
            throw new ArgumentException("Invalid Browser reset start.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, owner.TenantId, ct);
        var row = await OwnedAgents(db, owner.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct);
        if (state is null || row is null || state.Tenant.Revision != expectedSecurityRevision) return null;
        try { await RequireHumanSessionAsync(db, owner.TenantId, human, ct); } catch (AccessDeniedException) { return null; }
        var current = Owned(row);
        if (!TenantBrowserProfileRules.Matches(owner, current) || current.Instance.Revision != expectedInstanceRevision
            || current.Instance.Lifecycle != AgentInstanceLifecycle.Active || current.Instance.BrowserProfileReset?.Status == "pending"
            || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == reset.OperationId, ct)
            || !await TenantBrowserProfileRules.Allowed(state, owner, human.PrincipalId, new IamAuthorizer(this), true, ct)) return null;
        db.Attach(row.Instance);
        row.Instance.BrowserProfileResetJson = JsonSerializer.Serialize(reset, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        row.Instance.Revision++; row.Instance.UpdatedAtUtc = reset.RequestedAt.ToUnixTimeMilliseconds();
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == owner.TenantId, ct); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(Owned(row), tenant.Revision); }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
        catch (DbUpdateConcurrencyException) { return null; }
    }
    public async ValueTask<TenantBrowserProfileCommit?> TrySettleTenantProfileResetAsync(TenantBrowserAgentOwner owner, Guid operationId,
        string status, string reasonCode, DateTimeOffset at, Guid auditId, CancellationToken ct = default)
    {
        owner.Validate(); if (operationId == Guid.Empty || auditId == Guid.Empty) throw new ArgumentException("Exact reset and audit IDs are required.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == auditId, ct)) return null;
        var row = await OwnedAgents(db, owner.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct);
        if (row is null) return null; var current = Owned(row);
        if (!TenantBrowserProfileRules.Matches(owner, current) || current.Instance.BrowserProfileReset is not { } saved
            || saved.OperationId != operationId || saved.Status != "pending") return null;
        var reset = saved with { Status = status, ReasonCode = reasonCode, RecordedAt = at }; TenantBrowserProfileRules.Validate(reset);
        db.Attach(row.Instance);
        row.Instance.BrowserProfileResetJson = JsonSerializer.Serialize(reset, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        row.Instance.Revision++; row.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == owner.TenantId, ct); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, owner.TenantId, saved.PrincipalId, "browser.profile.reset.result", $"{owner.AgentInstanceId:D}:{operationId:D}", "allowed", reasonCode, at));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(Owned(row), tenant.Revision); }
        catch (DbUpdateConcurrencyException) { return null; }
    }
}
