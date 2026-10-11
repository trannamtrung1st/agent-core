using AgentCore.Contracts.Http;

namespace AgentCore.Api;

public sealed class SchedulerReadiness(TimeProvider time)
{
    private readonly object gate = new();
    private DateTimeOffset? lastPass;
    private DateTimeOffset? lastSuccess;
    private bool failed;
    public void Record(bool succeeded)
    {
        lock (gate)
        {
            lastPass = time.GetUtcNow();
            failed = !succeeded;
            if (succeeded) lastSuccess = lastPass;
        }
    }
    public SchedulerReadinessResponse Snapshot()
    {
        lock (gate)
        {
            var status = lastPass is null ? "starting" : time.GetUtcNow() - lastPass > TimeSpan.FromSeconds(10)
                ? "stale" : failed ? "degraded" : "ready";
            return new(status, lastPass?.ToString("O"), lastSuccess?.ToString("O"));
        }
    }
}
