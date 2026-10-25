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
        var owner = await WorkspaceOwnershipAsync(db, proof.Workspace, ct).ConfigureAwait(false);
        if (owner is null || owner.Revision != proof.OwnerRevision) return false;
        var allowed = owner.Writable
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
    private async ValueTask<TenantWorkspaceOwnership?> WorkspaceOwnershipAsync(AgentCoreDbContext db, AgentCore.Domain.Conversation.TenantWorkspaceReference owner, CancellationToken ct)
    {
        if (owner is AgentCore.Domain.Conversation.TenantWorkspaceReference.SpaceHome home)
        {
            var space = (await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == home.TenantId && s.SpaceId == home.SpaceId && s.DeletedAt == null, ct).ConfigureAwait(false))?.Domain();
            if (space is null || home.PrivateAlias && space.Kind != AgentCore.Domain.Spaces.SpaceKind.Private) return null;
            TenantAgent? agent = null;
            if (space.OwnerInstanceId is { } instance)
            {
                var key = instance.ToString("D");
                var row = await OwnedAgents(db, home.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == key, ct).ConfigureAwait(false);
                if (row is null || !await db.Set<SpaceMembershipRecord>().AnyAsync(m => m.TenantId == home.TenantId && m.SpaceId == home.SpaceId && m.InstanceId == key, ct).ConfigureAwait(false)) return null;
                agent = Owned(row);
            }
            return new(owner, space, agent, null);
        }
        if (owner is AgentCore.Domain.Conversation.TenantWorkspaceReference.SessionWorking working)
        {
            var key = working.SessionId.ToString("D");
            var row = await OwnedSessions(db, working.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == key, ct).ConfigureAwait(false);
            if (row is null) return null;
            var session = await SessionView(db, row, ct).ConfigureAwait(false);
            var instance = session.Owner.AgentInstanceId.ToString("D");
            var agentRow = await OwnedAgents(db, working.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == instance, ct).ConfigureAwait(false);
            if (agentRow is null) return null;
            var agent = Owned(agentRow);
            return TenantWorkspaceMutationRules.Matches(owner, agent, session) ? new(owner, null, agent, session) : null;
        }
        return null;
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
