using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class ArtifactRecordRow
{
    public string ArtifactId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string BlobKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long ByteSize { get; set; }
    public string Sha256Hex { get; set; } = "";
    public string? SourceAttachmentId { get; set; }
    public string? WorkspaceLogicalPath { get; set; }
    public long CreatedAtUtc { get; set; }
}

public sealed class SqliteArtifactStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    TimeProvider time,
    string blobRoot,
    long maxBytesEach = ArtifactLimits.MaxBytesEach,
    long maxBytesSession = ArtifactLimits.MaxBytesSession) : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _writers = new();
    private readonly ConcurrentDictionary<Guid, byte> _deleted = new();

    public bool Exists(Guid sessionId, Guid artifactId)
    {
        if (_deleted.ContainsKey(sessionId))
        {
            return false;
        }

        using var db = contexts.CreateDbContext();
        var key = artifactId.ToString("D");
        var sid = sessionId.ToString("D");
        return db.Artifacts.AsNoTracking().Any(row => row.ArtifactId == key && row.SessionId == sid);
    }

    public async ValueTask<ArtifactRecord> CreateAsync(
        Guid sessionId,
        string displayName,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        Guid? sourceAttachmentId,
        string? workspaceLogicalPath,
        CancellationToken cancellationToken = default)
    {
        AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
        Directory.CreateDirectory(blobRoot);
        ThrowIfDeleted(sessionId);
        if (bytes.Length > maxBytesEach)
        {
            throw AgentCoreErrors.ArtifactQuotaExceeded();
        }

        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = Path.Combine(blobRoot, $".partial-{Guid.NewGuid():N}");
        try
        {
            ThrowIfDeleted(sessionId);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Writer(sessionId).Token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            await using var db = await contexts.CreateDbContextAsync(linked.Token).ConfigureAwait(false);
            var used = await db.Artifacts.AsNoTracking()
                .Where(row => row.SessionId == sessionId.ToString("D"))
                .SumAsync(row => row.ByteSize, linked.Token)
                .ConfigureAwait(false);
            if (used + bytes.Length > maxBytesSession)
            {
                throw AgentCoreErrors.ArtifactQuotaExceeded();
            }

            var id = Guid.CreateVersion7();
            var key = AttachmentBlobKeys.For(sessionId, id);
            var finalPath = ResolvePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            await File.WriteAllBytesAsync(tempPath, bytes.ToArray(), linked.Token).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: false);
            var record = new ArtifactRecord(
                id,
                sessionId,
                WorkspaceFileNames.Sanitize(displayName),
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),
                sourceAttachmentId,
                workspaceLogicalPath,
                time.GetUtcNow());
            db.Artifacts.Add(ToRow(record, key));
            await db.SaveChangesAsync(linked.Token).ConfigureAwait(false);
            return record;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ArtifactRecord?> GetAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        if (_deleted.ContainsKey(sessionId))
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ArtifactId == artifactId.ToString("D") && item.SessionId == sessionId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public async ValueTask<IReadOnlyList<ArtifactRecord>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_deleted.ContainsKey(sessionId))
        {
            return [];
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Artifacts.AsNoTracking()
            .Where(row => row.SessionId == sessionId.ToString("D"))
            .OrderBy(row => row.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(FromRow).ToArray();
    }

    public async ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var record = await GetAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Artifact was not found.");
        var path = ResolvePath(AttachmentBlobKeys.For(sessionId, record.ArtifactId));
        if (!File.Exists(path))
        {
            throw AgentCoreErrors.NotFound("Artifact was not found.");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _deleted[sessionId] = 1;
        if (_writers.TryRemove(sessionId, out var writers))
        {
            await writers.CancelAsync().ConfigureAwait(false);
            writers.Dispose();
        }

        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.Artifacts.Where(row => row.SessionId == sessionId.ToString("D")).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                TryDelete(ResolvePath(row.BlobKey));
            }

            db.Artifacts.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var folder = Path.Combine(Path.GetFullPath(blobRoot), sessionId.ToString("N"));
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string ResolvePath(string key)
    {
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw AgentCoreErrors.Validation("Invalid blob key.");
        }

        var root = Path.GetFullPath(blobRoot);
        var path = Path.GetFullPath(Path.Combine(root, parts[0], parts[1]));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("Invalid blob key.");
        }

        return path;
    }

    private SemaphoreSlim Gate(Guid sessionId) => _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    private CancellationTokenSource Writer(Guid sessionId) =>
        _writers.GetOrAdd(sessionId, _ => new CancellationTokenSource());

    private void ThrowIfDeleted(Guid sessionId)
    {
        if (_deleted.ContainsKey(sessionId))
        {
            throw AgentCoreErrors.NotFound("Artifact was not found.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static ArtifactRecordRow ToRow(ArtifactRecord record, string blobKey) =>
        new()
        {
            ArtifactId = record.ArtifactId.ToString("D"),
            SessionId = record.SessionId.ToString("D"),
            BlobKey = blobKey,
            DisplayName = record.DisplayName,
            ContentType = record.ContentType,
            ByteSize = record.ByteSize,
            Sha256Hex = record.Sha256Hex,
            SourceAttachmentId = record.SourceAttachmentId?.ToString("D"),
            WorkspaceLogicalPath = record.WorkspaceLogicalPath,
            CreatedAtUtc = record.CreatedAt.ToUnixTimeMilliseconds()
        };

    private static ArtifactRecord FromRow(ArtifactRecordRow row) =>
        new(
            Guid.Parse(row.ArtifactId),
            Guid.Parse(row.SessionId),
            row.DisplayName,
            row.ContentType,
            row.ByteSize,
            row.Sha256Hex,
            string.IsNullOrEmpty(row.SourceAttachmentId) ? null : Guid.Parse(row.SourceAttachmentId),
            row.WorkspaceLogicalPath,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc));
}
