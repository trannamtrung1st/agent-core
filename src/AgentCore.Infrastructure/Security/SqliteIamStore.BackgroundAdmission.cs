using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<AgentRunAdmissionResult> AdmitTenantImmediateAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
        AgentRun child, Guid auditId, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(frame.Owner); if (auditId == Guid.Empty) throw new ArgumentException("An audit identity is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (root, parent) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var tenant = root.TenantId; var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
        var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        var agentRow = await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false);
        var agent = Owned(agentRow); var now = (clock ?? TimeProvider.System).GetUtcNow();
        var childRoot = TenantBackgroundAdmissionRules.Shape(frame, new(root.Domain(), SqliteMemoryStore.ToSnapshot(row, [])), parent, snapshot, child, agent, now);
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? (await FindDefinitionPublicationAsync(tenant, definition, agent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        await TenantBackgroundAdmissionRules.AuthorizeAsync(state, parent, current, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var key = AgentRunStoreMapping.BackgroundSourceKey(snapshot, child)!;
        var duplicate = await db.Activations.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant
            && a.AgentInstanceId == root.AgentInstanceId && a.ExecutorPrincipalId == root.ExecutorPrincipalId && a.BackgroundSourceKey == key, ct).ConfigureAwait(false);
        if (duplicate is not null)
        {
            var savedRow = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.ActivationId == duplicate.ActivationId, ct).ConfigureAwait(false);
            if (savedRow is null) throw new SecurityConflictException("background_id_conflict");
            var saved = AgentRunStoreMapping.ToDomain(savedRow);
            var savedSession = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleAsync(s => s.SessionId == savedRow.SessionId, ct).ConfigureAwait(false);
            if (!TenantBackgroundAdmissionRules.Same(saved, SqliteMemoryStore.ToSnapshot(savedSession, []), child, snapshot))
                throw new SecurityConflictException("background_id_conflict");
            return new(false, saved);
        }
        await StageTenantBackgroundGraph(db, childRoot, snapshot, child, ct).ConfigureAwait(false);
        var tenantRow = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        db.Entry(tenantRow).CurrentValues.SetValues(state.Tenant with { Revision = checked(state.Tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, tenant, frame.Owner.ExecutorPrincipalId!.Value, "agent-runs.background.admit",
            child.AgentRunId.ToString("D"), "allowed", "owned_parent_child_admitted", now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(true, child); }
        catch (DbUpdateConcurrencyException) { throw new SecurityConflictException("stale_revision"); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw new SecurityConflictException("identity_conflict"); }
    }
}
