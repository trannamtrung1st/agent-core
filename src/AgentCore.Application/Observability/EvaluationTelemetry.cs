using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Domain.Evaluations;
namespace AgentCore.Application.Observability;
public static class EvaluationTelemetry
{
    private static readonly Counter<long> Attempts = RuntimeTelemetry.Meter.CreateCounter<long>("evaluations.attempts");
    private static readonly Histogram<double> Duration = RuntimeTelemetry.Meter.CreateHistogram<double>("evaluations.duration_ms", "ms");
    public static void Record(EvaluationHook hook, EvaluationStatus status, EvaluationOutcome? outcome, double milliseconds)
    {
        var tags = new TagList { { "hook", hook.ToString() }, { "status", status.ToString() }, { "outcome", outcome?.ToString() ?? "pending" } };
        Attempts.Add(1, tags); Duration.Record(Math.Max(0, milliseconds), tags);
    }
}
