using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteArtifactStore : ITenantArtifactStore
{
    internal static IQueryable<ArtifactRecordRow> Owned(AgentCoreDbContext db, Guid tenant, Guid session) => db.Artifacts.AsNoTracking()
        .Where(a => a.TenantId == tenant && a.SessionId == session.ToString("D")
            && db.Set<TenantSessionRecord>().Any(s => s.TenantId == tenant && s.SessionId == a.SessionId
                && db.Sessions.Any(saved => saved.SessionId == s.SessionId && saved.DurablyDeletedAtUtc == null))
            && db.AgentRuns.Any(r => r.TenantId == tenant && r.AgentRunId == a.AgentRunId && r.SessionId == a.SessionId));
    private static async ValueTask RequireFrame(AgentCoreDbContext db, TenantRuntimeFrame frame, DateTimeOffset at, CancellationToken ct)
    {
        TenantRuntimeRules.RequireOwner(frame.Owner);
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == frame.Owner.TenantId && s.SessionId == frame.SessionId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("session_unavailable");
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == frame.RunId.ToString("D") && r.TenantId == root.TenantId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("run_unavailable");
        if (!await db.Sessions.AnyAsync(s => s.SessionId == root.SessionId && s.DurablyDeletedAtUtc == null, ct).ConfigureAwait(false)) throw new AccessDeniedException("session_unavailable");
        TenantRuntimeRules.Require(frame, root.Domain(), AgentRunStoreMapping.ToDomain(row), at);
    }
    private static bool OwnedSession(AgentCoreDbContext db, Guid session) => db.Set<TenantSessionRecord>().Any(s => s.SessionId == session.ToString("D"))
        || db.Artifacts.Any(a => a.SessionId == session.ToString("D") && a.TenantId != null);
    private void RequireUnowned(Guid session)
    { using var db = contexts.CreateDbContext(); if (OwnedSession(db, session)) throw new AccessDeniedException("tenant_context_required"); }
    private string TenantPath(Guid tenant, Guid session, Guid artifact)
    {
        AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
        var path = Path.GetFullPath(blobRoot);
        foreach (var part in TenantArtifactRules.BlobKey(tenant, session, artifact).Split('/'))
        {
            path = Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new AccessDeniedException("artifact_content_unavailable");
        }
        return path;
    }
    public async ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid artifactId, string displayName,
        string contentType, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        if (bytes.Length > Math.Min(maxBytesEach, ArtifactLimits.MaxBytesEach)) throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
        var frozen = bytes.ToArray();
        var next = TenantArtifactRules.Record(frame, artifactId, displayName, contentType, frozen, time.GetUtcNow());
        var gate = Gate(frame.SessionId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDeleted(frame.SessionId);
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await RequireFrame(db, frame, time.GetUtcNow(), ct).ConfigureAwait(false);
            var existing = await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(a => a.ArtifactId == artifactId.ToString("D"), ct).ConfigureAwait(false);
            if (existing is not null) return TenantArtifactRules.SamePublication(FromRow(existing), next) ? FromRow(existing) : throw new SecurityConflictException("artifact_publication_mismatch");
            var row = await StageTenantLocalAsync(db, frame, next, frozen, ct).ConfigureAwait(false);
            db.Artifacts.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return next;
        }
        finally { gate.Release(); }
    }
    // Caller owns the metadata/Run/receipt transaction. A private staged blob is not publication.
    internal async ValueTask<ArtifactRecordRow> StageTenantLocalAsync(AgentCoreDbContext db, TenantRuntimeFrame frame,
        ArtifactRecord next, byte[] frozen, CancellationToken ct)
    {
        if (next != TenantArtifactRules.Record(frame, next.ArtifactId, next.DisplayName, next.ContentType, frozen, next.CreatedAt)) throw new SecurityConflictException("artifact_publication_mismatch");
        await RequireFrame(db, frame, time.GetUtcNow(), ct).ConfigureAwait(false); ThrowIfDeleted(frame.SessionId);
        if (await db.Artifacts.AnyAsync(a => a.ArtifactId == next.ArtifactId.ToString("D"), ct).ConfigureAwait(false)) throw new SecurityConflictException("artifact_publication_mismatch");
        var used = await db.Artifacts.Where(a => a.SessionId == frame.SessionId.ToString("D")).SumAsync(a => a.ByteSize, ct).ConfigureAwait(false);
        if (frozen.Length > Math.Min(maxBytesEach, ArtifactLimits.MaxBytesEach) || used + frozen.Length > maxBytesSession) throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
        var path = TenantPath(frame.Owner.TenantId!.Value, frame.SessionId, next.ArtifactId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, frozen, ct).ConfigureAwait(false);
            if (File.Exists(path)) { _ = await ReadBytes(path, next, ct).ConfigureAwait(false); File.Delete(temp); }
            else File.Move(temp, path, overwrite: false);
            await RequireFrame(db, frame, time.GetUtcNow(), ct).ConfigureAwait(false);
            return ToRow(next, TenantArtifactRules.BlobKey(next.TenantId!.Value, next.SessionId, next.ArtifactId));
        }
        finally { TryDelete(temp); }
    }
    public async ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
    {
        if (_deleted.ContainsKey(session)) return null;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await Owned(db, tenant, session).SingleOrDefaultAsync(a => a.ArtifactId == artifact.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }
    public async ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before,
        int limit, Guid? run = null, CancellationToken ct = default)
    {
        TenantArtifactRules.Query(tenant, session, limit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = Owned(db, tenant, session);
        if (_deleted.ContainsKey(session)) query = query.Where(_ => false);
        if (allowed is not null) { var ids = allowed.Select(id => id.ToString("D")).ToArray(); query = query.Where(a => ids.Contains(a.ArtifactId)); }
        if (run is not null) query = query.Where(a => a.AgentRunId == run.Value.ToString("D"));
        if (before is { } cursor)
        {
            var anchor = await query.SingleOrDefaultAsync(a => a.ArtifactId == cursor.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            query = query.Where(a => a.CreatedAtUtc < anchor.CreatedAtUtc || a.CreatedAtUtc == anchor.CreatedAtUtc && string.Compare(a.ArtifactId, anchor.ArtifactId) < 0);
        }
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.ArtifactId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        var more = rows.Length > limit; return new(rows.Take(limit).Select(FromRow).ToArray(), more ? Guid.Parse(rows[limit - 1].ArtifactId) : null, more);
    }
    public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
    {
        var record = await FindTenantAsync(tenant, session, artifact, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return await ReadBytes(TenantPath(tenant, session, artifact), record, ct).ConfigureAwait(false);
    }
    private static async ValueTask<byte[]> ReadBytes(string path, ArtifactRecord record, CancellationToken ct)
    {
        if (record.ByteSize is < 0 or > ArtifactLimits.MaxBytesEach || !File.Exists(path)) throw new AccessDeniedException("artifact_content_unavailable");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length != record.ByteSize) throw new AccessDeniedException("artifact_content_unavailable");
        var bytes = new byte[checked((int)record.ByteSize)]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        TenantArtifactRules.RequireContent(record, bytes); return bytes;
    }
}
