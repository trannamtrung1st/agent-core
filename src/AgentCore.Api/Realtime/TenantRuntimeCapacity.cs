using Microsoft.Extensions.Options;

namespace AgentCore.Api.Realtime;

// Process capacity bounds Runtime lifetimes; it never authorizes a tenant operation.
public sealed class TenantRuntimeCapacity(IOptions<AgentCoreOptions> options)
{
    private readonly object gate = new();
    private readonly int limit = Math.Max(1, options.Value.MaxActiveSessions);
    private int reserved;
    private bool admitting = true;

    internal bool TryReserveTenantRuntime()
    {
        lock (gate)
        {
            if (!admitting || reserved >= limit) return false;
            reserved++; return true;
        }
    }
    internal void ReleaseTenantRuntime()
    {
        lock (gate) { if (reserved > 0) reserved--; }
    }
    internal void StopAdmissions()
    {
        lock (gate) admitting = false;
    }
}
