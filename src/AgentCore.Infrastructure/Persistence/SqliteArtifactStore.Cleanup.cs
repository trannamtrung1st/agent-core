using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteArtifactStore : ITenantArtifactCleanupStore
{
    public async ValueTask<TenantArtifactCleanupPage> ListDeletedArtifactOwnersAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || after == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = from owner in db.Set<TenantSessionRecord>().AsNoTracking()
                    join session in db.Sessions.AsNoTracking() on owner.SessionId equals session.SessionId
                    where session.DurablyDeletedAtUtc != null
                    select new { Owner = owner, DeletedAt = session.DurablyDeletedAtUtc!.Value };
        if (after is { } cursor) { var key = cursor.ToString("D"); query = query.Where(s => string.Compare(s.Owner.SessionId, key) > 0); }
        var rows = await query.OrderBy(s => s.Owner.SessionId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        var items = rows.Take(limit).Select(s => new TenantArtifactCleanupTarget(s.Owner.Domain(), DateTimeOffset.FromUnixTimeMilliseconds(s.DeletedAt))).ToArray();
        return new(items, rows.Length > limit ? items[^1].Owner.SessionId : null);
    }

    public async ValueTask<bool> CleanupDeletedArtifactsAsync(TenantArtifactCleanupTarget target, CancellationToken ct = default)
    {
        var owner = target.Owner;
        var gate = Gate(owner.SessionId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            var actual = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == owner.SessionId.ToString("D") && s.TenantId == owner.TenantId, ct).ConfigureAwait(false);
            if (actual is null || actual.Domain() != owner || !await db.Sessions.AnyAsync(s => s.SessionId == actual.SessionId
                && s.DurablyDeletedAtUtc == target.DeletedAt.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false)
                || await db.AgentRuns.AnyAsync(r => r.SessionId == actual.SessionId && r.Status != (int)AgentRunStatus.Completed
                    && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct).ConfigureAwait(false)) return false;
            // Deletion is irreversible; claimed publication can no longer pass its canonical frame checks.
            // PurgeOwner opens every component without following links and never creates a missing root.
            AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
            CanonicalWorkspaceFilesystem.PurgeOwner(Path.Combine(Path.GetFullPath(blobRoot), "tenants", owner.TenantId.ToString("N"), owner.SessionId.ToString("N")), ct);
            return true;
        }
        finally { gate.Release(); }
    }
}
