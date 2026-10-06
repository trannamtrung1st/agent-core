using AgentCore.Application.Sessions;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Experience;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryExperienceStore : IContinuityMaintenanceStore
{
    private readonly Dictionary<Guid, ContinuityMaintenanceSettings> continuitySettings = [];
    ValueTask<ContinuityMaintenanceSettings> IContinuityMaintenanceStore.ReadAsync(Guid id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (gate) return ValueTask.FromResult(continuitySettings.GetValueOrDefault(id) ?? new(id, null, 0, null)); }

    ValueTask<ContinuityMaintenanceSettings> IContinuityMaintenanceStore.ConfigureAsync(Guid id, long expectedRevision,
        int? intervalSeconds, CancellationToken ct, AdminEventAppend? audit)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedRevision < 0 || intervalSeconds is <= 0) throw AgentCoreErrors.Validation("Continuity maintenance settings are invalid.");
        lock (gate)
        {
            var current = continuitySettings.GetValueOrDefault(id) ?? new(id, null, 0, null);
            if (current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Continuity maintenance settings revision is stale.");
            var saved = current with { IntervalSeconds = intervalSeconds, Revision = current.Revision + 1 };
            if (audit is not null) events?.AppendWithinLock(audit);
            continuitySettings[id] = saved;
            return ValueTask.FromResult(saved);
        }
    }

    ValueTask<bool> IContinuityMaintenanceStore.TryClaimAsync(ContinuityMaintenanceSettings expected, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = continuitySettings.GetValueOrDefault(expected.AgentInstanceId) ?? new(expected.AgentInstanceId, null, 0, null);
            if (current.Revision != expected.Revision || current.LastMaintenanceAtUtc != expected.LastMaintenanceAtUtc) return ValueTask.FromResult(false);
            // Both adapters store millisecond precision, matching the existing durable timestamp convention.
            continuitySettings[expected.AgentInstanceId] = current with { LastMaintenanceAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()) };
            return ValueTask.FromResult(true);
        }
    }
}
