using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Observability;

public static class InitiativeEvaluationTelemetry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void RecordEvaluation(AgentContext context, AgentDecision decision, double elapsedMs)
    {
        var policy = context.Definition.InitiativePolicy;
        var lastUserAt = context.LastUserActivityAt ?? context.UtcNow;
        var silenceMs = Math.Max(0, (context.UtcNow - lastUserAt).TotalMilliseconds);
        var (evaluated, reasonCode, nextWaitMs) = Classify(decision);
        var payload = new Dictionary<string, object?>
        {
            ["agentId"] = context.Definition.Id,
            ["trigger"] = context.Trigger.Kind.ToString(),
            ["evaluated"] = evaluated,
            ["reasonCode"] = reasonCode,
            ["nextWaitMs"] = nextWaitMs,
            ["silenceMs"] = silenceMs,
            ["consecutiveProactiveSpeaks"] = context.ConsecutiveProactiveSpeaks,
            ["speaksThisSilencePeriod"] = context.SpeaksThisSilencePeriod,
            ["silentEvaluations"] = context.SilentEvaluations,
            ["consecutiveCap"] = policy.ConsecutiveCap,
            ["maxPerSilencePeriod"] = policy.MaxPerSilencePeriod,
            ["inactivityExceeded"] = context.InactivityExceeded,
            ["elapsedMs"] = elapsedMs
        };
        if (RuntimeTelemetry.IncludesConversationContent && TryModelReason(decision, out var reasonDetail))
        {
            payload["reasonDetail"] = reasonDetail;
        }

        RuntimeTelemetry.RecordDiagnostic("initiative_eval", elapsedMs, JsonSerializer.Serialize(payload, Json));
    }

    public static void RecordDisposition(
        AgentTrigger trigger,
        AgentDecision evaluated,
        bool admitted,
        string? blockReason = null)
    {
        var (kind, reasonCode, _) = Classify(evaluated);
        var payload = new
        {
            trigger = trigger.Kind.ToString(),
            evaluated = kind,
            evaluatedReasonCode = reasonCode,
            admitted,
            blockReason = blockReason ?? (admitted ? "admitted" : DispositionBlockReason(evaluated))
        };
        RuntimeTelemetry.RecordDiagnostic("initiative_disposition", 0, JsonSerializer.Serialize(payload, Json));
    }

    public static string DispositionBlockReason(AgentDecision decision) =>
        decision switch
        {
            StaySilent silent when silent.CountsTowardSilentCap => "semantic_silence",
            StaySilent silent => Classify(silent).ReasonCode switch
            {
                "provider_failed" => "provider_failed",
                "unparseable" => "unparseable",
                _ => "hard_cap"
            },
            _ => "semantic_silence"
        };

    private static (string Evaluated, string ReasonCode, int? NextWaitMs) Classify(AgentDecision decision) =>
        decision switch
        {
            Speak speak => ("speak", "model_speak", speak.NextWaitMs),
            RequestDeactivate => ("deactivate", "model_deactivate", null),
            StaySilent silent when !silent.CountsTowardSilentCap && silent.Reason.Contains("provider", StringComparison.OrdinalIgnoreCase) =>
                ("staySilent", "provider_failed", silent.NextWaitMs),
            StaySilent silent when !silent.CountsTowardSilentCap && silent.Reason.Contains("parse", StringComparison.OrdinalIgnoreCase) =>
                ("staySilent", "unparseable", silent.NextWaitMs),
            StaySilent silent when !silent.CountsTowardSilentCap =>
                ("staySilent", "infrastructure", silent.NextWaitMs),
            StaySilent silent => ("staySilent", "semantic_silence", silent.NextWaitMs),
            _ => ("unknown", "unknown", null)
        };

    private static bool TryModelReason(AgentDecision decision, out string? detail)
    {
        detail = decision switch
        {
            StaySilent silent => Clip(silent.Reason, 240),
            RequestDeactivate deactivate => Clip(deactivate.Reason, 240),
            _ => null
        };
        return detail is not null;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
