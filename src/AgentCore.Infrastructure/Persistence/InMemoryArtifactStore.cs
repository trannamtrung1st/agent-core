using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<Guid, ArtifactRecord> _items = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _deleted = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _writers = new();
    private readonly TimeProvider _time;
    private readonly long _maxEach;
    private readonly long _maxSession;

    public InMemoryArtifactStore(
        TimeProvider time,
        long maxBytesEach = ArtifactLimits.MaxBytesEach,
        long maxBytesSession = ArtifactLimits.MaxBytesSession)
    {
        _time = time;
        _maxEach = maxBytesEach;
        _maxSession = maxBytesSession;
    }

    public bool Exists(Guid sessionId, Guid artifactId) =>
        _items.TryGetValue(artifactId, out var record) && record.SessionId == sessionId && !_deleted.ContainsKey(sessionId);

    public async ValueTask<ArtifactRecord> CreateAsync(
        Guid sessionId,
        string displayName,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        Guid? sourceAttachmentId,
        string? workspaceLogicalPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeleted(sessionId);
        if (bytes.Length > _maxEach)
        {
            throw AgentCoreErrors.ArtifactQuotaExceeded();
        }

        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDeleted(sessionId);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Writer(sessionId).Token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var used = _items.Values.Where(item => item.SessionId == sessionId).Sum(item => item.ByteSize);
            if (used + bytes.Length > _maxSession)
            {
                throw AgentCoreErrors.ArtifactQuotaExceeded();
            }

            var id = Guid.CreateVersion7();
            var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
            var record = new ArtifactRecord(
                id,
                sessionId,
                WorkspaceFileNames.Sanitize(displayName),
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                bytes.Length,
                hash,
                sourceAttachmentId,
                workspaceLogicalPath,
                _time.GetUtcNow());
            _items[id] = record;
            _blobs[id] = bytes.ToArray();
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask<ArtifactRecord?> GetAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Exists(sessionId, artifactId))
        {
            return ValueTask.FromResult<ArtifactRecord?>(null);
        }

        return ValueTask.FromResult<ArtifactRecord?>(_items[artifactId]);
    }

    public ValueTask<IReadOnlyList<ArtifactRecord>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_deleted.ContainsKey(sessionId))
        {
            return ValueTask.FromResult<IReadOnlyList<ArtifactRecord>>([]);
        }

        IReadOnlyList<ArtifactRecord> items = _items.Values
            .Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        return ValueTask.FromResult(items);
    }

    public ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Exists(sessionId, artifactId) || !_blobs.TryGetValue(artifactId, out var bytes))
        {
            throw AgentCoreErrors.NotFound("Artifact was not found.");
        }

        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _deleted[sessionId] = 1;
        if (_writers.TryRemove(sessionId, out var writers))
        {
            await writers.CancelAsync().ConfigureAwait(false);
            writers.Dispose();
        }

        foreach (var pair in _items)
        {
            if (pair.Value.SessionId == sessionId)
            {
                _items.TryRemove(pair.Key, out _);
                _blobs.TryRemove(pair.Key, out _);
            }
        }
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
}
