using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryArtifactStore
{
    // Caller holds the incumbent IAM + Session gate. Validate the whole batch before publication.
    internal void PublishBrowserBatchLocal(TenantRuntimeFrame frame, IReadOnlyList<TenantBrowserArtifactFile> files, DateTimeOffset at, CancellationToken ct, DateTimeOffset? expiresAt = null)
    {
        var sessions = _sessions ?? throw new AccessDeniedException("tenant_context_required");
        lock (sessions.AdmissionGate)
        {
            var session = sessions.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var run = sessions.ReadTenantRun(frame.Owner.TenantId.Value, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, _time.GetUtcNow()); ThrowIfDeleted(frame.SessionId); ct.ThrowIfCancellationRequested();
            var items = files.Select(f => (Bytes: f.Content.ToArray(), File: f)).Select(f => (f.Bytes,
                Record: TenantArtifactRules.Record(frame, f.File.ArtifactId, f.File.DisplayName, f.File.ContentType, f.Bytes, at))).ToArray();
            if (items.Any(i => _items.ContainsKey(i.Record.ArtifactId))) throw new SecurityConflictException("artifact_publication_mismatch");
            if (items.Any(i => i.Bytes.Length > _maxEach) || _items.Values.Where(i => i.SessionId == frame.SessionId).Sum(i => i.ByteSize)
                + items.Sum(i => i.Record.ByteSize) > _maxSession) throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
            TenantRuntimeRules.Require(frame, session.Owner, run, _time.GetUtcNow()); ct.ThrowIfCancellationRequested();
            if (expiresAt <= _time.GetUtcNow()) throw new AccessDeniedException("approval_expired");
            foreach (var item in items) { _blobs[item.Record.ArtifactId] = item.Bytes; _items[item.Record.ArtifactId] = item.Record; }
        }
    }
}
