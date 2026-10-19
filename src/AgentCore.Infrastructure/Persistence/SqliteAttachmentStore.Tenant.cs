using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAttachmentStore : ITenantAttachmentStore
{
    internal static IQueryable<AttachmentRecordRow> Owned(AgentCoreDbContext db, Guid tenant, Guid session) => db.Attachments.Where(a => a.TenantId == tenant && a.SessionId == session.ToString("D")
        && db.Set<TenantSessionRecord>().Any(s => s.TenantId == tenant && s.SessionId == a.SessionId && db.Sessions.Any(saved => saved.SessionId == s.SessionId && saved.DurablyDeletedAtUtc == null)));
    internal static async ValueTask RequireTenantOwner(AgentCoreDbContext db, TenantSessionRoot owner, CancellationToken ct)
    {
        var actual = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == owner.TenantId && s.SessionId == owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if (actual is null || actual.Domain() != owner || !await db.Sessions.AnyAsync(s => s.SessionId == actual.SessionId && s.DurablyDeletedAtUtc == null, ct).ConfigureAwait(false))
            throw new AccessDeniedException("resource_unavailable");
    }
    private void RequireUnowned(Guid session)
    {
        using var db = contexts.CreateDbContext();
        if (db.Set<TenantSessionRecord>().Any(s => s.SessionId == session.ToString("D")) || db.Attachments.Any(a => a.SessionId == session.ToString("D") && a.TenantId != null))
            throw new AccessDeniedException("tenant_context_required");
    }
    private CanonicalWorkspaceFilesystem TenantFiles(Guid tenant, Guid session)
    {
        AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
        return new(Path.Combine(Path.GetFullPath(blobRoot), "tenants", tenant.ToString("N"), session.ToString("N")), "/working");
    }
    internal async ValueTask<AttachmentRecordRow> StageTenant(AgentCoreDbContext db, TenantSessionRoot owner, TenantAttachmentUpload upload, CancellationToken ct)
    {
        await RequireTenantOwner(db, owner, ct).ConfigureAwait(false); var item = upload.Record; TenantAttachmentRules.Content(item, upload.Bytes);
        if (item != TenantAttachmentRules.Pending(owner, item.AttachmentId, item.UploadRequestId!.Value, item.UploadedByPrincipalId!.Value,
            item.DisplayName, item.ContentType, item.Readable, upload.Bytes, item.CreatedAt)) throw new SecurityConflictException("attachment_upload_mismatch");
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var used = await Owned(db, owner.TenantId, owner.SessionId).Where(a => a.State == nameof(AttachmentState.Bound) || a.ExpiresAtUtc > now).SumAsync(a => (long?)a.ByteSize, ct).ConfigureAwait(false) ?? 0;
        if (used + item.ByteSize > AttachmentLimits.MaxBytesSession) throw TenantAttachmentRules.Quota();
        var files = TenantFiles(owner.TenantId, owner.SessionId); var path = "/working/" + item.AttachmentId.ToString("N");
        try
        {
            if (!await files.TryWriteNewAsync(path, upload.Bytes, AttachmentLimits.MaxBytesEach, AttachmentLimits.MaxBytesSession, ct,
                token => RequireTenantOwner(db, owner, token)).ConfigureAwait(false))
                TenantAttachmentRules.Content(item, await files.ReadUnlinkedAsync(path, AttachmentLimits.MaxBytesEach, ct).ConfigureAwait(false));
        }
        catch (AgentCoreException error) when (error.Code == "WorkspaceQuotaExceeded") { throw TenantAttachmentRules.Quota(); }
        return ToRow(item);
    }
    public async ValueTask<TenantAttachmentUpload> PrepareUploadTenantAsync(TenantSessionRoot owner, Guid id, Guid request, Guid actor, string name, string type, Stream content, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await RequireTenantOwner(db, owner, ct).ConfigureAwait(false);
        var result = await TenantAttachmentIntake.Read(owner, id, request, actor, name, type, content, time, ct).ConfigureAwait(false);
        await RequireTenantOwner(db, owner, ct).ConfigureAwait(false); return result;
    }
    internal static AttachmentRecord Domain(AttachmentRecordRow row) => FromRow(row);
    public async ValueTask<bool> OwnsTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await Owned(db, tenant, session).AnyAsync(a => a.AttachmentId == attachment.ToString("D"), ct).ConfigureAwait(false); }
    public async ValueTask<AttachmentRecord?> FindTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var row = await Owned(db, tenant, session).AsNoTracking().SingleOrDefaultAsync(a => a.AttachmentId == attachment.ToString("D") && a.ContentDeletedAtUtc == null
            && (a.State == nameof(AttachmentState.Bound) || a.ExpiresAtUtc > now), ct).ConfigureAwait(false); return row is null ? null : FromRow(row);
    }
    public async ValueTask<TenantAttachmentPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, CancellationToken ct = default)
    {
        TenantAttachmentRules.Query(tenant, session, limit); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var query = Owned(db, tenant, session).AsNoTracking().Where(a => a.ContentDeletedAtUtc == null && (a.State == nameof(AttachmentState.Bound) || a.ExpiresAtUtc > now));
        if (allowed is not null) { var ids = allowed.Select(id => id.ToString("D")).ToArray(); query = query.Where(a => ids.Contains(a.AttachmentId)); }
        if (before is { } cursor)
        {
            var anchor = await query.SingleOrDefaultAsync(a => a.AttachmentId == cursor.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            query = query.Where(a => a.CreatedAtUtc > anchor.CreatedAtUtc || a.CreatedAtUtc == anchor.CreatedAtUtc && string.Compare(a.AttachmentId, anchor.AttachmentId) > 0);
        }
        var rows = await query.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.AttachmentId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Take(limit).Select(FromRow).ToArray(), rows.Length > limit ? Guid.Parse(rows[limit - 1].AttachmentId) : null, rows.Length > limit);
    }
    public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    {
        var item = await FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        byte[] bytes;
        try { bytes = await TenantFiles(tenant, session).ReadUnlinkedAsync("/working/" + attachment.ToString("N"), AttachmentLimits.MaxBytesEach, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AgentCoreException or IOException or UnauthorizedAccessException) { throw new AccessDeniedException("attachment_content_unavailable"); }
        TenantAttachmentRules.Content(item, bytes); return bytes;
    }
}
