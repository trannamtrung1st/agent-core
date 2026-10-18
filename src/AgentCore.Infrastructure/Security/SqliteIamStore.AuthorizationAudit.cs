using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAuthorizationAuditStore
{
    public async ValueTask<bool> TryRecordWorkspaceAuthorizationAsync(TenantWorkspaceHumanAuthorization proof, CancellationToken ct = default)
    {
        TenantWorkspaceMutationRules.Validate(proof);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, proof.Workspace.TenantId, ct).ConfigureAwait(false);
        if (state is null || state.Tenant.Revision != proof.SecurityRevision) return false;
        try { await RequireHumanSessionAsync(db, proof.Workspace.TenantId, proof.Human, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { return false; }
        var row = await OwnedAgents(db, proof.Workspace.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == proof.Workspace.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null) return false;
        var agent = Owned(row); TenantSession? session = null;
        if (proof.Workspace.SessionId is { } id)
        {
            var owner = await OwnedSessions(db, proof.Workspace.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false);
            if (owner is null) return false;
            session = await SessionView(db, owner, ct).ConfigureAwait(false);
        }
        if (agent.Instance.Revision != proof.InstanceRevision || !TenantWorkspaceMutationRules.Matches(proof.Workspace, agent, session)) return false;
        var allowed = TenantWorkspaceMutationRules.Writable(agent, session)
            && await TenantWorkspaceMutationRules.Allowed(state, proof.Workspace, proof.Human.PrincipalId, proof.Permission, new IamAuthorizer(this), ct).ConfigureAwait(false);
        if (allowed != (proof.Audit.Decision == "allowed")) return false;
        var existing = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == proof.Audit.AuditId, ct).ConfigureAwait(false);
        if (existing is not null) return existing == proof.Audit;
        db.Add(proof.Audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return false; }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { return false; }
    }
    public async ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantOperationAuditRules.Validate(audit); var e = audit.Execution!;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var revision = await db.Set<Tenant>().Where(t => t.TenantId == audit.TenantId).Select(t => (long?)t.Revision).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, e.Context.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == e.Context.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (revision is null || row is null || !TenantOperationAuditRules.Matches(audit, AgentRunStoreMapping.ToDomain(row), revision.Value)) return false;
        var existing = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == audit.AuditId, ct).ConfigureAwait(false);
        if (existing is not null) return existing == audit;
        db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
}
