namespace AgentCore.Api.Realtime;

public sealed partial class SessionHost
{
    private int _tenantRuntimeReservations;
    internal bool TryReserveTenantRuntime()
    {
        lock (_gate)
        {
            if (!_admitting || _live.Count + _tenantRuntimeReservations >= Math.Max(1, _options.MaxActiveSessions)) return false;
            _tenantRuntimeReservations++; return true;
        }
    }
    internal void ReleaseTenantRuntime()
    { lock (_gate) { if (_tenantRuntimeReservations > 0) _tenantRuntimeReservations--; } }
}
