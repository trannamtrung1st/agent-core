using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using System.Text.Json;

namespace AgentCore.Application.Events;

// Retry state lives in the existing durable decision JSON, independently of the frozen subscription.
public static class EventFilterRecovery
{
    public const int MaxAttempts = 5;

    public static EventFilterResult Evaluate(IEventFilterEvaluator evaluator, EventSubscriptionSnapshot snapshot,
        EventFilterResult? previous, JsonElement envelope, DateTimeOffset now, CancellationToken ct)
    {
        if (previous is { Retryable: false }) return previous;
        if (previous?.RetryAtUtc > now) return previous;
        var result = snapshot.ExpressionVersion == "js-expression-v1"
            ? evaluator.Evaluate(snapshot.FilterExpression, envelope, ct)
            : new EventFilterResult(null, "error", "filter-expression-version");
        var attempt = (previous?.Attempt ?? 0) + 1;
        if (!result.Retryable) return result with { Attempt = attempt };
        return attempt >= MaxAttempts
            ? result with { Attempt = attempt, Code = "filter-retry-exhausted", Status = "error" }
            : result with { Attempt = attempt, Status = "retryPending", RetryAtUtc = now.AddSeconds(1 << (attempt - 1)) };
    }
}
