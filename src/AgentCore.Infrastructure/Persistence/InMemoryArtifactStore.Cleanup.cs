using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryArtifactStore : ITenantArtifactCleanupStore
{
    public ValueTask<TenantArtifactCleanupPage> ListDeletedArtifactOwnersAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100 || after == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(limit));
        return ValueTask.FromResult((_sessions ?? throw new InvalidOperationException("Artifact Session ownership is unavailable.")).DeletedArtifactOwners(after, limit));
    }

    public async ValueTask<bool> CleanupDeletedArtifactsAsync(TenantArtifactCleanupTarget target, CancellationToken ct = default)
    {
        var gate = Gate(target.Owner.SessionId); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessions = _sessions ?? throw new InvalidOperationException("Artifact Session ownership is unavailable.");
            lock (sessions.AdmissionGate)
            {
                ct.ThrowIfCancellationRequested();
                if (!sessions.CanCleanupDeletedArtifacts(target)) return false;
                foreach (var item in _items.Values.Where(a => a.TenantId == target.Owner.TenantId && a.SessionId == target.Owner.SessionId))
                {
                    ct.ThrowIfCancellationRequested();
                    _blobs.TryRemove(item.ArtifactId, out _);
                }
                return true;
            }
        }
        finally { gate.Release(); }
    }
}
