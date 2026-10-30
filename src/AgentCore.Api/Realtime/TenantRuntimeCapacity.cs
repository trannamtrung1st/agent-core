using Microsoft.Extensions.Options;

namespace AgentCore.Api.Realtime;

// Process capacity bounds Runtime lifetimes; it never authorizes a tenant operation.
public sealed class TenantRuntimeCapacity(IOptions<AgentCoreOptions> options)
{
    private readonly object gate = new();
    private readonly AgentCoreOptions limits = options.Value;
    private int reserved;
    private bool admitting = true;

    internal bool TryReserveTenantRuntime()
    {
        lock (gate)
        {
            if (!admitting || reserved >= Math.Max(1, limits.MaxActiveSessions))
            {
                AgentCore.Application.Observability.RuntimeTelemetry.RecordRuntimeAdmission(!admitting ? "stopping" : "capacity", 0);
                return false;
            }
            reserved++;
            AgentCore.Application.Observability.RuntimeTelemetry.RecordRuntimeAdmission("accepted", 1);
            return true;
        }
    }
    internal void ReleaseTenantRuntime()
    {
        lock (gate) { if (reserved > 0) { reserved--; AgentCore.Application.Observability.RuntimeTelemetry.RecordRuntimeAdmission("released", -1); } }
    }
    internal void StopAdmissions()
    {
        lock (gate) admitting = false;
    }
}
