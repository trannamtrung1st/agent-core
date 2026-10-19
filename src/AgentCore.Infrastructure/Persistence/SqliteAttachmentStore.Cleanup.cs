using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAttachmentStore : ITenantAttachmentCleanupStore
{
    private static string CleanupKey(TenantAttachmentCleanupTarget target) => target.AttachmentId == Guid.Empty ? "session:" + target.SessionId.ToString("D") : "attachment:" + target.AttachmentId.ToString("D");
    public async ValueTask<TenantAttachmentCleanupPage> ListAttachmentCleanupAsync(string? after, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var attachmentAfter = InMemoryAttachmentStore.ParseCleanupCursor(after, "attachment:"); var sessionAfter = InMemoryAttachmentStore.ParseCleanupCursor(after, "session:");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var expired = db.Attachments.AsNoTracking().Where(a => a.TenantId != null && a.ContentDeletedAtUtc == null && a.State == nameof(AttachmentState.Pending) && a.ExpiresAtUtc <= now
            && db.Set<TenantSessionRecord>().Any(s => s.TenantId == a.TenantId && s.SessionId == a.SessionId && db.Sessions.Any(root => root.SessionId == s.SessionId && root.DurablyDeletedAtUtc == null)));
        if (after?.StartsWith("session:", StringComparison.Ordinal) == true) expired = expired.Where(_ => false);
        if (attachmentAfter is { } aid) { var key = aid.ToString("D"); expired = expired.Where(a => string.Compare(a.AttachmentId, key) > 0); }
        var rows = (await expired.OrderBy(a => a.AttachmentId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false))
            .Select(a => new TenantAttachmentCleanupTarget(a.TenantId!.Value, Guid.Parse(a.SessionId), Guid.Parse(a.AttachmentId), a.Sha256Hex));
        var deleted = db.Set<TenantSessionRecord>().AsNoTracking().Where(s => db.Sessions.Any(root => root.SessionId == s.SessionId && root.DurablyDeletedAtUtc != null));
        if (sessionAfter is { } sid) { var key = sid.ToString("D"); deleted = deleted.Where(s => string.Compare(s.SessionId, key) > 0); }
        var owners = (await deleted.OrderBy(s => s.SessionId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false)).Select(s => new TenantAttachmentCleanupTarget(s.TenantId, Guid.Parse(s.SessionId), Guid.Empty, ""));
        var page = rows.Concat(owners).Where(a => after is null || string.CompareOrdinal(CleanupKey(a), after) > 0).OrderBy(CleanupKey, StringComparer.Ordinal).Take(limit + 1).ToArray();
        return new(page.Take(limit).ToArray(), page.Length > limit ? CleanupKey(page[limit - 1]) : null);
    }
    public async ValueTask<bool> CleanupAttachmentContentAsync(TenantAttachmentCleanupTarget target, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await db.Set<TenantSessionRecord>().SingleOrDefaultAsync(s => s.TenantId == target.TenantId && s.SessionId == target.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if (root is null) return false;
        if (target.AttachmentId == Guid.Empty)
        {
            if (!await db.Sessions.AnyAsync(s => s.SessionId == root.SessionId && s.DurablyDeletedAtUtc != null, ct).ConfigureAwait(false)) return false;
            AttachmentBlobKeys.EnsureSafeRoot(blobRoot); CanonicalWorkspaceFilesystem.PurgeOwner(TenantFiles(target.TenantId, target.SessionId).PhysicalRoot, ct);
            var rows = await db.Attachments.Where(a => a.TenantId == target.TenantId && a.SessionId == root.SessionId && a.ContentDeletedAtUtc == null).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var row in rows) row.ContentDeletedAtUtc = time.GetUtcNow().ToUnixTimeMilliseconds();
        }
        else
        {
            var row = await db.Attachments.SingleOrDefaultAsync(a => a.TenantId == target.TenantId && a.SessionId == root.SessionId && a.AttachmentId == target.AttachmentId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || row.Sha256Hex != target.Sha256Hex || row.State != nameof(AttachmentState.Pending) || row.ExpiresAtUtc > time.GetUtcNow().ToUnixTimeMilliseconds()) return false;
            try { TenantFiles(target.TenantId, target.SessionId).Delete("/working/" + target.AttachmentId.ToString("N"), false, ct); }
            catch (AgentCoreException error) when (error.Code == "NotFound") { }
            row.ContentDeletedAtUtc = time.GetUtcNow().ToUnixTimeMilliseconds();
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true;
    }
}
