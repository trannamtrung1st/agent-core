using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class AttachmentBlobKeys
{
    public static string For(Guid sessionId, Guid attachmentId) =>
        $"{sessionId:N}/{attachmentId:N}";

    public static void EnsureSafeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var localMarker = $"{Path.DirectorySeparatorChar}local{Path.DirectorySeparatorChar}";
        if (full.Contains($"{Path.DirectorySeparatorChar}local{Path.DirectorySeparatorChar}tdp-workspace", StringComparison.OrdinalIgnoreCase)
            || full.Contains($"{Path.AltDirectorySeparatorChar}local{Path.AltDirectorySeparatorChar}tdp-workspace", StringComparison.OrdinalIgnoreCase)
            || full.EndsWith($"{localMarker}tdp-workspace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attachment blob root must not be under local/tdp-workspace.");
        }
    }
}

internal static class AttachmentStreamIntake
{
    public static async Task<IntakeResult> ReadAsync(
        Stream content,
        long sessionUsedBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var prefix = new byte[512];
        var prefixLength = 0;
        var suffix = new byte[32];
        var suffixLength = 0;
        var total = 0L;
        var chunk = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > AttachmentLimits.MaxBytesEach)
                {
                    throw AgentCoreErrors.Validation("Attachment exceeds 25 MiB.");
                }

                if (sessionUsedBytes + total > AttachmentLimits.MaxBytesSession)
                {
                    throw AgentCoreErrors.Validation("Session attachment quota of 250 MiB would be exceeded.");
                }

                hash.AppendData(chunk, 0, read);
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                var copy = Math.Min(read, prefix.Length - prefixLength);
                if (copy > 0)
                {
                    chunk.AsSpan(0, copy).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += copy;
                }

                var take = Math.Min(read, suffix.Length);
                if (take == read)
                {
                    if (suffixLength + read <= suffix.Length)
                    {
                        chunk.AsSpan(0, read).CopyTo(suffix.AsSpan(suffixLength));
                        suffixLength += read;
                    }
                    else
                    {
                        var keep = suffix.Length - read;
                        if (keep > 0)
                        {
                            Buffer.BlockCopy(suffix, suffixLength - keep, suffix, 0, keep);
                        }

                        chunk.AsSpan(0, read).CopyTo(suffix.AsSpan(suffix.Length - read));
                        suffixLength = suffix.Length;
                    }
                }
                else
                {
                    Buffer.BlockCopy(chunk, read - take, suffix, 0, take);
                    suffixLength = take;
                }
            }
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        buffer.Position = 0;
        return new IntakeResult(
            buffer,
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            prefix.AsSpan(0, prefixLength).ToArray(),
            suffix.AsSpan(0, suffixLength).ToArray());
    }
}

internal sealed record IntakeResult(
    MemoryStream Bytes,
    long Length,
    string Sha256Hex,
    byte[] Prefix,
    byte[] Suffix);

public sealed class InMemoryAttachmentStore(TimeProvider time) : IAttachmentStore
{
    private readonly ConcurrentDictionary<Guid, AttachmentRecord> _items = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();

    public async ValueTask<AttachmentRecord> UploadPendingAsync(
        Guid sessionId,
        string displayName,
        string declaredContentType,
        Stream content,
        bool allowStoreUnread,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SweepExpiredAsync(cancellationToken).ConfigureAwait(false);
            var used = UsedBytes(sessionId);
            var intake = await AttachmentStreamIntake.ReadAsync(content, used, cancellationToken).ConfigureAwait(false);
            await using (intake.Bytes.ConfigureAwait(false))
            {
                var inspect = AttachmentClassification.Inspect(
                    intake.Prefix,
                    intake.Suffix,
                    intake.Length,
                    declaredContentType,
                    displayName,
                    allowStoreUnread);
                if (!inspect.Accepted)
                {
                    throw AgentCoreErrors.Validation(inspect.Rejection ?? "Attachment was rejected.");
                }

                var now = time.GetUtcNow();
                var id = Guid.CreateVersion7();
                var record = new AttachmentRecord(
                    id,
                    sessionId,
                    AttachmentBlobKeys.For(sessionId, id),
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
                _items[id] = record;
                _blobs[id] = intake.Bytes.ToArray();
                return record;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask AbortPendingAsync(Guid sessionId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_items.TryGetValue(attachmentId, out var record) || record.SessionId != sessionId)
            {
                throw AgentCoreErrors.NotFound("Session was not found.");
            }

            if (record.State != AttachmentState.Pending)
            {
                throw AgentCoreErrors.Validation("Only pending attachments can be aborted.");
            }

            _items.TryRemove(attachmentId, out _);
            _blobs.TryRemove(attachmentId, out _);
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
            foreach (var pair in _items)
            {
                if (pair.Value.SessionId == sessionId && pair.Value.State == AttachmentState.Pending && pair.Value.StageForNextTurn)
                {
                    _items[pair.Key] = pair.Value with { StageForNextTurn = false };
                }
            }

            foreach (var id in attachmentIds)
            {
                _items[id] = _items[id] with { StageForNextTurn = true };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask ValidateBindableAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attachmentIds.Count > AttachmentLimits.MaxPerMessage)
        {
            throw AgentCoreErrors.Validation("A message may include at most 10 attachments.");
        }

        if (attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            throw AgentCoreErrors.Validation("Attachment ids must be unique.");
        }

        var now = time.GetUtcNow();
        foreach (var id in attachmentIds)
        {
            if (!_items.TryGetValue(id, out var record) || record.SessionId != sessionId)
            {
                throw AgentCoreErrors.NotFound("Session was not found.");
            }

            if (record.State == AttachmentState.Bound)
            {
                continue;
            }

            if (record.ExpiresAt is { } expires && expires <= now)
            {
                throw AgentCoreErrors.Validation("Pending attachment expired.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<AttachmentRecord>> BindToEntryAsync(
        Guid sessionId,
        Guid entryId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        await ValidateBindableAsync(sessionId, attachmentIds, cancellationToken).ConfigureAwait(false);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ValidateBindableAsync(sessionId, attachmentIds, cancellationToken).ConfigureAwait(false);
            var bound = new List<AttachmentRecord>(attachmentIds.Count);
            var now = time.GetUtcNow();
            foreach (var id in attachmentIds)
            {
                var record = _items[id];
                if (record.State == AttachmentState.Bound)
                {
                    if (record.EntryId != entryId)
                    {
                        throw AgentCoreErrors.Validation("Attachment is already bound to another message.");
                    }

                    bound.Add(record);
                    continue;
                }

                if (record.ExpiresAt is { } expires && expires <= now)
                {
                    throw AgentCoreErrors.Validation("Pending attachment expired.");
                }

                var next = record with
                {
                    State = AttachmentState.Bound,
                    EntryId = entryId,
                    StageForNextTurn = false,
                    ExpiresAt = null,
                    BoundAt = now
                };
                _items[id] = next;
                bound.Add(next);
            }

            return bound;
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<AttachmentRecord>> ListStagedPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        IReadOnlyList<AttachmentRecord> items = _items.Values
            .Where(item =>
                item.SessionId == sessionId
                && item.State == AttachmentState.Pending
                && item.StageForNextTurn
                && (item.ExpiresAt is null || item.ExpiresAt > now))
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        return ValueTask.FromResult(items);
    }

    public ValueTask<IReadOnlyList<AttachmentRecord>> ListForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AttachmentRecord> items = _items.Values
            .Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        return ValueTask.FromResult(items);
    }

    public bool Exists(Guid sessionId, Guid attachmentId) =>
        _items.TryGetValue(attachmentId, out var record) && record.SessionId == sessionId;

    public ValueTask<AttachmentRecord?> GetAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(attachmentId, out var record) || record.SessionId != sessionId)
        {
            return ValueTask.FromResult<AttachmentRecord?>(null);
        }

        return ValueTask.FromResult<AttachmentRecord?>(record);
    }

    public ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(attachmentId, out var record)
            || record.SessionId != sessionId
            || !_blobs.TryGetValue(attachmentId, out var bytes))
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var pair in _items)
        {
            if (pair.Value.SessionId == sessionId)
            {
                _items.TryRemove(pair.Key, out _);
                _blobs.TryRemove(pair.Key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        foreach (var pair in _items)
        {
            if (pair.Value.State == AttachmentState.Pending
                && pair.Value.ExpiresAt is { } expires
                && expires <= now)
            {
                _items.TryRemove(pair.Key, out _);
                _blobs.TryRemove(pair.Key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    private long UsedBytes(Guid sessionId) =>
        _items.Values.Where(item => item.SessionId == sessionId).Sum(item => item.ByteSize);

    private SemaphoreSlim Gate(Guid sessionId) =>
        _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
}
