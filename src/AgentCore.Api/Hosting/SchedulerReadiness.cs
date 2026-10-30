using AgentCore.Contracts.Http;

namespace AgentCore.Api;

public sealed class SchedulerReadiness(TimeProvider time)
{
    private readonly object gate = new();
    private DateTimeOffset? lastPass;
    private DateTimeOffset? lastSuccess;
    private bool failed;
    private readonly Dictionary<string, (DateTimeOffset? LastPass, DateTimeOffset? LastSuccess, bool Failed)> workers =
        new(StringComparer.Ordinal) { ["runs"] = default, ["task-hints"] = default, ["workflows"] = default, ["email-watch"] = default };
    public void RecordWorker(string worker, bool succeeded)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(worker, out var current)) throw new ArgumentOutOfRangeException(nameof(worker));
            var now = time.GetUtcNow();
            workers[worker] = (now, succeeded ? now : current.LastSuccess, !succeeded);
        }
        AgentCore.Application.Observability.RuntimeTelemetry.RecordWorkerPass(worker, succeeded);
    }
    public IReadOnlyDictionary<string, SchedulerReadinessResponse> WorkersSnapshot()
    {
        lock (gate) return workers.ToDictionary(pair => pair.Key, pair => {
            var value = pair.Value;
            var staleAfter = pair.Key == "email-watch" ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(10);
            var status = value.LastPass is null ? "starting" : time.GetUtcNow() - value.LastPass > staleAfter
                ? "stale" : value.Failed ? "degraded" : "ready";
            return new SchedulerReadinessResponse(status, value.LastPass?.ToString("O"), value.LastSuccess?.ToString("O"));
        });
    }
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
