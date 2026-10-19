using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantSessionForegroundStore
{
    private async Task<TenantSession> RequireForeground(AgentCoreDbContext db, TenantSessionForeground request, bool live, CancellationToken ct)
    {
        await RequireHumanSessionAsync(db, request.Owner.TenantId, request.Human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, request.Owner.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var root = await OwnedSessions(db, request.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == request.Owner.SessionId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var saved = await SessionView(db, root, ct).ConfigureAwait(false);
        var agent = await OwnedAgents(db, request.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSessionForegroundRules.RequireAsync(request, saved, Owned(agent), state, new IamAuthorizer(this), live, ct).ConfigureAwait(false);
        return saved;
    }
    private static async Task AuditForeground(AgentCoreDbContext db, TenantSessionForeground request, Guid auditId, DateTimeOffset at, CancellationToken ct)
    {
        if (auditId == Guid.Empty) throw new ArgumentException("Audit identity is required.");
        var state = await db.Set<Tenant>().SingleAsync(t => t.TenantId == request.Owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(state).CurrentValues.SetValues(state with { Revision = checked(state.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, request.Owner.TenantId, request.Human.PrincipalId, "sessions.continue-in-chat", request.Owner.SessionId.ToString("D"), "allowed", "chat_surface_added", at));
    }
    public async ValueTask SaveTenantRuntimeForegroundAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantSessionForeground request, Guid auditId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await RequireForeground(db, request, live: true, ct).ConfigureAwait(false);
        var (root, _) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        if (root.Domain() != saved.Owner) throw new AccessDeniedException("tenant_context_mismatch");
        await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, snapshot, expectedRevision, ct, foreground: true).ConfigureAwait(false);
        if (!saved.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) await AuditForeground(db, request, auditId, (clock ?? TimeProvider.System).GetUtcNow(), ct).ConfigureAwait(false);
        await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false);
    }
    public async ValueTask ContinueTenantQuiescentAsync(TenantSessionForeground request, Guid auditId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await RequireForeground(db, request, live: false, ct).ConfigureAwait(false);
        if (await db.AgentRuns.AnyAsync(r => r.SessionId == request.Owner.SessionId.ToString("D") && r.Status == (int)AgentRunStatus.Running, ct).ConfigureAwait(false))
            throw new SecurityConflictException("session_in_use");
        if (saved.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) return;
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        var row = await db.Sessions.SingleAsync(s => s.SessionId == request.Owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
        row.Surfaces = (int)SessionOrigin.ContinueInChat(saved.Snapshot.Surfaces); row.Revision = checked(row.Revision + 1); row.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        await AuditForeground(db, request, auditId, at, ct).ConfigureAwait(false);
        await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false);
    }
}
