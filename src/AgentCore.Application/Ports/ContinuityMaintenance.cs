using AgentCore.Application.Admin;
using AgentCore.Domain.Experience;

namespace AgentCore.Application.Ports;

public interface IContinuityMaintenanceStore
{
    ValueTask<ContinuityMaintenanceSettings> ReadAsync(Guid instanceId, CancellationToken ct = default);
    ValueTask<ContinuityMaintenanceSettings> ConfigureAsync(Guid instanceId, long expectedRevision,
        int? intervalSeconds, CancellationToken ct = default, AdminEventAppend? audit = null);
    // Configuration revision and previous claim timestamp must both match atomically.
    ValueTask<bool> TryClaimAsync(ContinuityMaintenanceSettings expected, DateTimeOffset now, CancellationToken ct = default);
}
