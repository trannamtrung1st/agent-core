using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class AttachmentRecordRow
{
    public string AttachmentId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string BlobKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long ByteSize { get; set; }
    public string Sha256Hex { get; set; } = "";
    public string State { get; set; } = "Pending";
    public bool Readable { get; set; }
    public string? EntryId { get; set; }
    public bool StageForNextTurn { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? ExpiresAtUtc { get; set; }
    public long? BoundAtUtc { get; set; }
}

public sealed class MessageAttachmentRow
{
    public string EntryId { get; set; } = "";
    public string AttachmentId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Ordinal { get; set; }
}

public sealed class SqliteAttachmentStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    TimeProvider time,
    string blobRoot) : IAttachmentStore
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();

    public async ValueTask<AttachmentRecord> UploadPendingAsync(
        Guid sessionId,
        string displayName,
        string declaredContentType,
        Stream content,
        bool allowStoreUnread,
        CancellationToken cancellationToken = default)
    {
        AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
        Directory.CreateDirectory(blobRoot);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = Path.Combine(blobRoot, $".partial-{Guid.NewGuid():N}");
        try
        {
            await SweepExpiredAsync(cancellationToken).ConfigureAwait(false);
            var used = await UsedBytesAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var intake = await AttachmentStreamIntake.ReadAsync(content, used, cancellationToken).ConfigureAwait(false);
            await using (intake.Bytes.ConfigureAwait(false))
            {
                var inspect = AttachmentClassification.Inspect(
                    intake.Prefix,
                    intake.Suffix,
                    intake.Length,
                    declaredContentType,
                    allowStoreUnread);
                if (!inspect.Accepted)
                {
                    throw AgentCoreErrors.Validation(inspect.Rejection ?? "Attachment was rejected.");
                }

                var now = time.GetUtcNow();
                var id = Guid.CreateVersion7();
                var key = AttachmentBlobKeys.For(sessionId, id);
                var finalPath = ResolvePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    await intake.Bytes.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, finalPath, overwrite: false);
                var record = new AttachmentRecord(
                    id,
                    sessionId,
                    key,
                    AttachmentClassification.SanitizeDisplayName(displayName),
                    inspect.ContentType,
                    intake.Length,
                    intake.Sha256Hex,
                    AttachmentState.Pending,
                    inspect.Readable,
                    null,
                    false,
                    now,
                    now + AttachmentLimits.PendingTtl,
                    null);
                try
                {
                    await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    db.Attachments.Add(ToRow(record));
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    TryDelete(finalPath);
                    throw;
                }

                return record;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            TryDelete(tempPath);
            gate.Release();
        }
    }

    public async ValueTask AbortPendingAsync(Guid sessionId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Attachments.FirstOrDefaultAsync(
                    item => item.AttachmentId == attachmentId.ToString("D") && item.SessionId == sessionId.ToString("D"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                throw AgentCoreErrors.NotFound("Session was not found.");
            }

            if (!string.Equals(row.State, nameof(AttachmentState.Pending), StringComparison.Ordinal))
            {
                throw AgentCoreErrors.Validation("Only pending attachments can be aborted.");
            }

            db.Attachments.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            TryDelete(ResolvePath(row.BlobKey));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StageForNextTurnAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        await ValidateBindableAsync(sessionId, attachmentIds, cancellationToken).ConfigureAwait(false);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var session = sessionId.ToString("D");
            var pending = await db.Attachments
                .Where(item => item.SessionId == session && item.State == nameof(AttachmentState.Pending))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var wanted = attachmentIds.Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var row in pending)
            {
                row.StageForNextTurn = wanted.Contains(row.AttachmentId);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ValidateBindableAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        if (attachmentIds.Count > AttachmentLimits.MaxPerMessage)
        {
            throw AgentCoreErrors.Validation("A message may include at most 10 attachments.");
        }

        if (attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            throw AgentCoreErrors.Validation("Attachment ids must be unique.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var id in attachmentIds)
        {
            var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(
                    item => item.AttachmentId == id.ToString("D"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null || row.SessionId != sessionId.ToString("D"))
            {
                throw AgentCoreErrors.NotFound("Session was not found.");
            }

            if (string.Equals(row.State, nameof(AttachmentState.Bound), StringComparison.Ordinal))
            {
                continue;
            }

            if (row.ExpiresAtUtc is { } expires && expires <= now)
            {
                throw AgentCoreErrors.Validation("Pending attachment expired.");
            }
        }
    }

    public async ValueTask<IReadOnlyList<AttachmentRecord>> BindToEntryAsync(
        Guid sessionId,
        Guid entryId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var bound = new List<AttachmentRecord>(attachmentIds.Count);
            var ordinal = 0;
            foreach (var id in attachmentIds)
            {
                var row = await db.Attachments.FirstOrDefaultAsync(
                        item => item.AttachmentId == id.ToString("D"),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (row is null || row.SessionId != sessionId.ToString("D"))
                {
                    throw AgentCoreErrors.NotFound("Session was not found.");
                }

                if (string.Equals(row.State, nameof(AttachmentState.Bound), StringComparison.Ordinal))
                {
                    if (row.EntryId != entryId.ToString("D"))
                    {
                        throw AgentCoreErrors.Validation("Attachment is already bound to another message.");
                    }

                    bound.Add(FromRow(row));
                    ordinal++;
                    continue;
                }

                if (row.ExpiresAtUtc is { } expires && expires <= now.ToUnixTimeMilliseconds())
                {
                    throw AgentCoreErrors.Validation("Pending attachment expired.");
                }

                row.State = nameof(AttachmentState.Bound);
                row.EntryId = entryId.ToString("D");
                row.StageForNextTurn = false;
                row.ExpiresAtUtc = null;
                row.BoundAtUtc = now.ToUnixTimeMilliseconds();
                db.MessageAttachments.Add(new MessageAttachmentRow
                {
                    EntryId = entryId.ToString("D"),
                    AttachmentId = id.ToString("D"),
                    SessionId = sessionId.ToString("D"),
                    Ordinal = ordinal++
                });
                bound.Add(FromRow(row));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return bound;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AttachmentRecord>> ListStagedPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var rows = await db.Attachments.AsNoTracking()
            .Where(item =>
                item.SessionId == sessionId.ToString("D")
                && item.State == nameof(AttachmentState.Pending)
                && item.StageForNextTurn
                && (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now))
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(FromRow).ToArray();
    }

    public async ValueTask<IReadOnlyList<AttachmentRecord>> ListForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.AsNoTracking()
            .Where(item => item.SessionId == sessionId.ToString("D"))
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(FromRow).ToArray();
    }

    public async ValueTask<AttachmentRecord?> GetAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(
                item => item.AttachmentId == attachmentId.ToString("D") && item.SessionId == sessionId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public async ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var record = await GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        var path = ResolvePath(record.BlobKey);
        if (!File.Exists(path))
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var session = sessionId.ToString("D");
            var rows = await db.Attachments.Where(item => item.SessionId == session).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var links = await db.MessageAttachments.Where(item => item.SessionId == session).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            db.MessageAttachments.RemoveRange(links);
            db.Attachments.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                TryDelete(ResolvePath(row.BlobKey));
            }

            var folder = Path.Combine(blobRoot, sessionId.ToString("N"));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var expired = await db.Attachments
            .Where(item => item.State == nameof(AttachmentState.Pending) && item.ExpiresAtUtc != null && item.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count == 0)
        {
            return;
        }

        db.Attachments.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in expired)
        {
            TryDelete(ResolvePath(row.BlobKey));
        }
    }

    private async Task<long> UsedBytesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Attachments.Where(item => item.SessionId == sessionId.ToString("D"))
            .SumAsync(item => (long?)item.ByteSize, cancellationToken)
            .ConfigureAwait(false) ?? 0;
    }

    private string ResolvePath(string blobKey)
    {
        if (blobKey.Contains("..", StringComparison.Ordinal) || blobKey.Contains(Path.DirectorySeparatorChar) && !blobKey.Contains('/'))
        {
            throw AgentCoreErrors.Validation("Invalid blob key.");
        }

        var parts = blobKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out _)
            || !Guid.TryParseExact(parts[1], "N", out _))
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

    private SemaphoreSlim Gate(Guid sessionId) =>
        _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

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

    private static AttachmentRecordRow ToRow(AttachmentRecord record) =>
        new()
        {
            AttachmentId = record.AttachmentId.ToString("D"),
            SessionId = record.SessionId.ToString("D"),
            BlobKey = record.BlobKey,
            DisplayName = record.DisplayName,
            ContentType = record.ContentType,
            ByteSize = record.ByteSize,
            Sha256Hex = record.Sha256Hex,
            State = record.State.ToString(),
            Readable = record.Readable,
            EntryId = record.EntryId?.ToString("D"),
            StageForNextTurn = record.StageForNextTurn,
            CreatedAtUtc = record.CreatedAt.ToUnixTimeMilliseconds(),
            ExpiresAtUtc = record.ExpiresAt?.ToUnixTimeMilliseconds(),
            BoundAtUtc = record.BoundAt?.ToUnixTimeMilliseconds()
        };

    private static AttachmentRecord FromRow(AttachmentRecordRow row) =>
        new(
            Guid.Parse(row.AttachmentId),
            Guid.Parse(row.SessionId),
            row.BlobKey,
            row.DisplayName,
            row.ContentType,
            row.ByteSize,
            row.Sha256Hex,
            Enum.Parse<AttachmentState>(row.State),
            row.Readable,
            string.IsNullOrEmpty(row.EntryId) ? null : Guid.Parse(row.EntryId),
            row.StageForNextTurn,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            row.ExpiresAtUtc is { } expires ? DateTimeOffset.FromUnixTimeMilliseconds(expires) : null,
            row.BoundAtUtc is { } bound ? DateTimeOffset.FromUnixTimeMilliseconds(bound) : null);
}
