using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAttachmentStore : ITenantAttachmentCleanupStore
{
    private static string CleanupKey(TenantAttachmentCleanupTarget target) => target.AttachmentId == Guid.Empty ? "session:" + target.SessionId.ToString("D") : "attachment:" + target.AttachmentId.ToString("D");
    public ValueTask<TenantAttachmentCleanupPage> ListAttachmentCleanupAsync(string? after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var sessions = _tenantSessions ?? throw new InvalidOperationException("Attachment ownership is unavailable.");
        lock (sessions.AdmissionGate) lock (TenantGate)
        {
            var expired = _items.Values.Where(a => a.TenantId is not null && a.State == AttachmentState.Pending && a.ExpiresAt <= time.GetUtcNow() && !_cleaned.Contains(a.AttachmentId)
                && sessions.OwnsTenantSession(a.TenantId.Value, a.SessionId)).Select(a => new TenantAttachmentCleanupTarget(a.TenantId!.Value, a.SessionId, a.AttachmentId, a.Sha256Hex));
            var deletedPage = sessions.DeletedArtifactOwners(ParseCleanupCursor(after, "session:"), limit);
            var deleted = deletedPage.Items.Select(a => new TenantAttachmentCleanupTarget(a.Owner.TenantId, a.Owner.SessionId, Guid.Empty, ""));
            var rows = expired.Concat(deleted).Where(a => after is null || string.CompareOrdinal(CleanupKey(a), after) > 0).OrderBy(CleanupKey, StringComparer.Ordinal).Take(limit + 1).ToArray();
            var more = rows.Length > limit || deletedPage.NextCursor is not null;
            return ValueTask.FromResult(new TenantAttachmentCleanupPage(rows.Take(limit).ToArray(), more && rows.Length > 0 ? CleanupKey(rows[Math.Min(limit, rows.Length) - 1]) : null));
        }
    }
    internal static Guid? ParseCleanupCursor(string? after, string prefix)
    {
        if (after is null) return null;
        if (!(after.StartsWith("attachment:", StringComparison.Ordinal) || after.StartsWith("session:", StringComparison.Ordinal)) || !Guid.TryParseExact(after[(after.IndexOf(':') + 1)..], "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid attachment cleanup cursor.");
        return after.StartsWith(prefix, StringComparison.Ordinal) ? id : null;
    }
    public ValueTask<bool> CleanupAttachmentContentAsync(TenantAttachmentCleanupTarget target, CancellationToken ct = default)
    {
        var sessions = _tenantSessions ?? throw new InvalidOperationException("Attachment ownership is unavailable.");
        lock (sessions.AdmissionGate) lock (TenantGate)
        {
            ct.ThrowIfCancellationRequested();
            if (target.AttachmentId == Guid.Empty)
            {
                var owner = sessions.RetainedTenantSessionOwnerSnapshot(target.SessionId);
                if (owner?.TenantId != target.TenantId || !sessions.TenantSessionDeleted(target.TenantId, target.SessionId)) return ValueTask.FromResult(false);
                foreach (var item in _items.Values.Where(a => a.TenantId == target.TenantId && a.SessionId == target.SessionId)) { _blobs.TryRemove(item.AttachmentId, out _); _cleaned.Add(item.AttachmentId); }
                return ValueTask.FromResult(true);
            }
            if (sessions.RetainedTenantSessionOwnerSnapshot(target.SessionId)?.TenantId != target.TenantId) return ValueTask.FromResult(false);
            var row = RetainedTenant(target.TenantId, target.SessionId, target.AttachmentId);
            if (row is null || row.Sha256Hex != target.Sha256Hex || row.State != AttachmentState.Pending || row.ExpiresAt > time.GetUtcNow()) return ValueTask.FromResult(false);
            _blobs.TryRemove(row.AttachmentId, out _); _cleaned.Add(row.AttachmentId); return ValueTask.FromResult(true);
        }
    }
}
