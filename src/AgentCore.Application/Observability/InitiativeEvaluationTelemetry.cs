using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Observability;

public static class InitiativeEvaluationTelemetry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Record(AgentContext context, AgentDecision decision, double elapsedMs)
    {
        var policy = context.Definition.InitiativePolicy;
        var lastUserAt = context.LastUserActivityAt ?? context.UtcNow;
        var silenceMs = Math.Max(0, (context.UtcNow - lastUserAt).TotalMilliseconds);
        var (kind, reason, nextWaitMs) = Describe(decision);
        var payload = new
        {
            agentId = context.Definition.Id,
            trigger = context.Trigger.Kind.ToString(),
            decision = kind,
            reason,
            nextWaitMs,
            silenceMs,
            consecutiveProactiveSpeaks = context.ConsecutiveProactiveSpeaks,
            speaksThisSilencePeriod = context.SpeaksThisSilencePeriod,
            silentEvaluations = context.SilentEvaluations,
            consecutiveCap = policy.ConsecutiveCap,
            maxPerSilencePeriod = policy.MaxPerSilencePeriod,
            inactivityExceeded = context.InactivityExceeded,
            elapsedMs
        };
        RuntimeTelemetry.Record("initiative_eval", elapsedMs, JsonSerializer.Serialize(payload, Json));
    }

    private static (string Kind, string Reason, int? NextWaitMs) Describe(AgentDecision decision) =>
        decision switch
        {
            Speak => ("speak", "Initiative evaluation chose speak.", null),
            StaySilent silent => ("staySilent", silent.Reason, silent.NextWaitMs),
            RequestDeactivate deactivate => ("deactivate", deactivate.Reason, null),
            _ => ("unknown", "Unrecognized initiative decision.", null)
        };
}
