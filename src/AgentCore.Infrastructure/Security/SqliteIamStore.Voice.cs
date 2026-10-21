using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantVoiceModeStore
{
    public async ValueTask<TenantSession> ActivateTenantVoiceAsync(TenantVoiceModeChange request, Guid auditId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lease = request.Lease; await RequireHumanSessionAsync(db, lease.Owner.TenantId, lease.Human, ct);
        var state = await ReadTenantAsync(db, lease.Owner.TenantId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var root = await OwnedSessions(db, lease.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == lease.Owner.SessionId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var saved = await SessionView(db, root, ct);
        var parent = await OwnedAgents(db, lease.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantVoiceRules.RequireAsync(saved, Owned(parent), state, lease.Human, new IamAuthorizer(this), ct);
        TenantVoiceRules.Shape(request, saved, Owned(parent), state);
        if (auditId == Guid.Empty) throw new ArgumentException("Use a fresh voice audit identity.");
        if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status == (int)AgentRunStatus.Running, ct)) throw new SecurityConflictException("session_in_use");
        var row = await db.Sessions.SingleAsync(s => s.SessionId == root.SessionId, ct);
        var at = (clock ?? TimeProvider.System).GetUtcNow(); row.Mode = nameof(SessionMode.Voice); row.PendingMode = null;
        row.Revision = checked(row.Revision + 1); row.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        db.Add(new AuthorizationAudit(auditId, lease.Owner.TenantId, lease.Human.PrincipalId, "sessions.voice.activate", root.SessionId, "allowed", "voice_attachment_activated", at));
        await CommitReceiptAsync(db, tx, ct); return await SessionView(db, root, ct);
    }
    public async ValueTask RetireTenantVoiceAsync(TenantVoiceLease lease, long expectedRevision, CancellationToken ct = default)
    {
        if (!lease.Retiring) throw new AccessDeniedException("voice_lease_unavailable");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var root = await OwnedSessions(db, lease.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == lease.Owner.SessionId.ToString("D"), ct);
        if (root is null) return;
        if (root.Domain() != lease.Owner) throw new AccessDeniedException("voice_lease_unavailable");
        var row = await db.Sessions.SingleAsync(s => s.SessionId == root.SessionId, ct);
        if (row.Mode == nameof(SessionMode.Text) && row.PendingMode is null) return;
        if (row.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status == (int)AgentRunStatus.Running, ct)) throw new SecurityConflictException("session_in_use");
        row.Mode = nameof(SessionMode.Text); row.PendingMode = null; row.Revision = checked(row.Revision + 1);
        row.UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        await CommitReceiptAsync(db, tx, ct);
    }
}
