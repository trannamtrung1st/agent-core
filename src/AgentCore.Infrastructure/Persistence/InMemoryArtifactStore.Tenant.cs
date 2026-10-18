using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryArtifactStore : ITenantArtifactStore
{
    private InMemoryMemoryStore? _sessions;
    internal InMemoryArtifactStore BindTenantAuthority(InMemoryMemoryStore sessions)
    {
        if (_sessions is not null && !ReferenceEquals(_sessions, sessions)) throw new InvalidOperationException("Artifacts must share actual Session ownership.");
        _sessions = sessions; return this;
    }
    private bool OwnedSession(Guid session) => _sessions?.RetainedTenantSessionOwnerSnapshot(session) is not null
        || _items.Values.Any(a => a.SessionId == session && a.TenantId is not null);
    private void RequireUnowned(Guid session)
    { if (OwnedSession(session)) throw new AccessDeniedException("tenant_context_required"); }
    internal bool OwnsTenantArtifact(Guid tenant, Guid artifact) => _items.TryGetValue(artifact, out var item)
        && item.TenantId == tenant && !_deleted.ContainsKey(item.SessionId) && _sessions?.OwnsTenantSession(tenant, item.SessionId) == true
        && item.AgentRunId is { } run && _sessions.ReadTenantRun(tenant, run)?.SessionId == item.SessionId;

    public async ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid artifactId, string displayName,
        string contentType, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(frame.Owner);
        if (bytes.Length > Math.Min(_maxEach, ArtifactLimits.MaxBytesEach)) throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
        var frozen = bytes.ToArray();
        var next = TenantArtifactRules.Record(frame, artifactId, displayName, contentType, frozen, _time.GetUtcNow());
        var gate = Gate(frame.SessionId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return PublishTenantLocal(frame, next, frozen, true, ct);
        }
        finally { gate.Release(); }
    }
    internal ArtifactRecord PublishTenantLocal(TenantRuntimeFrame frame, ArtifactRecord next, byte[] frozen, bool allowRetry, CancellationToken ct)
    {
        var sessions = _sessions ?? throw new AccessDeniedException("tenant_context_required");
        lock (sessions.AdmissionGate)
        {
            var tenant = frame.Owner.TenantId!.Value;
            var session = sessions.ReadTenantSession(tenant, frame.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            var run = sessions.ReadTenantRun(tenant, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, _time.GetUtcNow()); ct.ThrowIfCancellationRequested(); ThrowIfDeleted(frame.SessionId);
            if (next != TenantArtifactRules.Record(frame, next.ArtifactId, next.DisplayName, next.ContentType, frozen, next.CreatedAt)) throw new SecurityConflictException("artifact_publication_mismatch");
            if (_items.TryGetValue(next.ArtifactId, out var existing))
                return allowRetry && TenantArtifactRules.SamePublication(existing, next) ? existing : throw new SecurityConflictException("artifact_publication_mismatch");
            if (frozen.Length > _maxEach || _items.Values.Where(a => a.SessionId == frame.SessionId).Sum(a => a.ByteSize) + frozen.Length > _maxSession)
                throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
            _blobs[next.ArtifactId] = frozen; _items[next.ArtifactId] = next; return next;
        }
    }
    public ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryGetValue(artifact, out var item) && item.TenantId == tenant && item.SessionId == session
            && OwnsTenantArtifact(tenant, artifact) ? item : null);
    }
    public ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before,
        int limit, Guid? run = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantArtifactRules.Query(tenant, session, limit);
        var visible = allowed?.ToHashSet();
        var rows = _items.Values.Where(a => a.TenantId == tenant && a.SessionId == session && (run is null || a.AgentRunId == run)
            && (visible is null || visible.Contains(a.ArtifactId)) && OwnsTenantArtifact(tenant, a.ArtifactId))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ArtifactId.ToString("D"), StringComparer.Ordinal).ToArray();
        var offset = before is null ? 0 : Array.FindIndex(rows, a => a.ArtifactId == before) + 1;
        if (before is not null && offset == 0) throw new AccessDeniedException("resource_unavailable");
        var selected = rows.Skip(offset).Take(limit + 1).ToArray(); var more = selected.Length > limit;
        return ValueTask.FromResult(new ArtifactPage(selected.Take(limit).ToArray(), more ? selected[limit - 1].ArtifactId : null, more));
    }
    public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default)
    {
        var record = await FindTenantAsync(tenant, session, artifact, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!_blobs.TryGetValue(artifact, out var bytes)) throw new AccessDeniedException("artifact_content_unavailable");
        var frozen = bytes.ToArray(); TenantArtifactRules.RequireContent(record, frozen); ct.ThrowIfCancellationRequested(); return frozen;
    }
}
