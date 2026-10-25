using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantSessionContextStore
{
    private async Task<(TenantSession Session, TenantSecurityState Security, bool Replay)> RequireContextSwitch(AgentCoreDbContext db, TenantContextSwitch request, bool live, CancellationToken ct)
    {
        if (request.Human is { } human) await RequireHumanSessionAsync(db, request.Owner.TenantId, human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, request.Owner.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var root = await OwnedSessions(db, request.Owner.TenantId).SingleOrDefaultAsync(s => s.SessionId == request.Owner.SessionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var saved = await SessionView(db, root, ct).ConfigureAwait(false);
        var agent = await OwnedAgents(db, root.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        if (request.Agent is { } frame)
        {
            var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
            await TenantSessionContextRules.RequireAgentAsync(request, saved, Owned(agent), run, await TenantAgentDefinitionBaseline(db, Owned(agent), ct).ConfigureAwait(false),
                state, new IamAuthorizer(this), (clock ?? TimeProvider.System).GetUtcNow(), ct).ConfigureAwait(false);
        }
        var destination = await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == root.TenantId && s.SpaceId == request.SpaceId && s.DeletedAt == null, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        var member = await db.Set<SpaceMembershipRecord>().SingleOrDefaultAsync(m => m.TenantId == root.TenantId && m.SpaceId == request.SpaceId && m.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false);
        var environment = request.EnvironmentId is { } id ? (await db.Set<SpaceEnvironmentRecord>().SingleOrDefaultAsync(e => e.TenantId == root.TenantId && e.SpaceId == request.SpaceId && e.EnvironmentId == id && e.DeletedAt == null, ct).ConfigureAwait(false))?.Domain() : null;
        var replay = TenantSessionContextRules.IsReplay(request, saved.Snapshot.ContextSelection);
        var check = replay ? request with { ExpectedSecurityRevision = state.Tenant.Revision, ExpectedSessionRevision = saved.Snapshot.Revision,
            ExpectedSelectionRevision = saved.Snapshot.ContextSelection!.Revision, ExpectedInstanceRevision = agent.Instance.Revision } : request;
        await TenantSessionContextRules.RequireAsync(check, saved, Owned(agent), state, new IamAuthorizer(this), destination.Domain(), member?.Domain([], []), environment, live, ct).ConfigureAwait(false);
        if (!replay)
        {
            var lease = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.SessionId == root.SessionId, ct).ConfigureAwait(false);
            if (lease is not null) TenantSandboxContextRules.RequireRetired(SandboxRuntimePersistence.Value(lease));
        }
        return (saved, state, replay);
    }
    private static long AuditContext(AgentCoreDbContext db, TenantContextSwitch request, TenantSecurityState state, Guid auditId, DateTimeOffset at)
    {
        if (auditId == Guid.Empty) throw new ArgumentException("An audit identity is required.");
        var tenant = db.Set<Tenant>().Local.SingleOrDefault(t => t.TenantId == request.Owner.TenantId);
        if (tenant is null) { tenant = state.Tenant; db.Attach(tenant); }
        var revision = checked(state.Tenant.Revision + 1);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = revision });
        db.Add(new AuthorizationAudit(auditId, request.Owner.TenantId, request.ActorPrincipalId, "sessions.context.switch", request.Owner.SessionId.ToString("D"), "allowed", "context_selection_changed", at));
        return revision;
    }
    public async ValueTask<TenantContextSwitchResult> SwitchTenantQuiescentContextAsync(TenantContextSwitch request, Guid auditId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (saved, state, replay) = await RequireContextSwitch(db, request, false, ct).ConfigureAwait(false);
        if (replay) return new(saved.Snapshot.ContextSelection!, saved.Snapshot.Revision, state.Tenant.Revision, "applied");
        var session = request.Owner.SessionId.ToString("D");
        if (await db.AgentRuns.AnyAsync(r => r.SessionId == session && r.Status == (int)AgentRunStatus.Running, ct).ConfigureAwait(false)) throw new SecurityConflictException("session_in_use");
        var at = (clock ?? TimeProvider.System).GetUtcNow(); var selection = TenantSessionContextRules.Selection(request, saved.Snapshot.ContextSelection!, null, at);
        var snapshot = saved.Snapshot with { ContextSelection = selection, Revision = checked(saved.Snapshot.Revision + 1), UpdatedAt = at };
        var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == session, ct).ConfigureAwait(false);
        SqliteMemoryStore.ApplySession(row, snapshot); var securityRevision = AuditContext(db, request, state, auditId, at);
        await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false); return new(selection, snapshot.Revision, securityRevision, "applied");
    }
    public async ValueTask<TenantContextSwitchResult> SaveTenantRuntimeContextAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantContextSwitch request, Guid auditId, CancellationToken ct = default)
    {
        if (request.Agent is { } requestingFrame && requestingFrame != frame) throw new AccessDeniedException("tenant_context_mismatch");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (saved, state, replay) = await RequireContextSwitch(db, request, true, ct).ConfigureAwait(false);
        var (root, _) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        if (root.Domain() != saved.Owner) throw new AccessDeniedException("tenant_context_mismatch");
        if (replay) return new(saved.Snapshot.ContextSelection!, saved.Snapshot.Revision, state.Tenant.Revision, "pending_next_turn");
        var at = snapshot.ContextSelection?.UpdatedAt ?? throw new AccessDeniedException("space_context_required");
        var selection = TenantSessionContextRules.Selection(request, saved.Snapshot.ContextSelection!, frame.RunId, at);
        if (snapshot.ContextSelection != selection) throw new AccessDeniedException("context_selection_changed");
        await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, snapshot, expectedRevision, ct, contextSwitch: true).ConfigureAwait(false);
        var securityRevision = AuditContext(db, request, state, auditId, at); await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false);
        return new(selection, snapshot.Revision, securityRevision, "pending_next_turn");
    }
}
