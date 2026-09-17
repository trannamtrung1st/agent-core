using System.Text.Json;
using AgentCore.Application.Agents;
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
        var (evaluated, reasonCode, nextWaitMs, intent) = Classify(decision);
        var payload = new Dictionary<string, object?>
        {
            ["agentId"] = context.Definition.Id,
            ["trigger"] = context.Trigger.Kind.ToString(),
            ["decision"] = evaluated,
            ["evaluated"] = evaluated,
            ["reasonCode"] = reasonCode,
            ["intent"] = intent,
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
        if (RuntimeTelemetry.IncludesConversationContent)
        {
            if (TryModelReason(decision, out var reasonDetail))
            {
                payload["reasonDetail"] = reasonDetail;
            }

            if (decision is Speak { Plan: { } plan })
            {
                payload["objective"] = Clip(plan.Objective, 240);
            }
        }

        RuntimeTelemetry.RecordDiagnostic("initiative_eval", elapsedMs, JsonSerializer.Serialize(payload, Json));
    }

    public static void RecordDisposition(
        AgentTrigger trigger,
        AgentDecision evaluated,
        bool admitted,
        string? blockReason = null)
    {
        var (kind, reasonCode, _, intent) = Classify(evaluated);
        var payload = new
        {
            trigger = trigger.Kind.ToString(),
            decision = kind,
            evaluated = kind,
            evaluatedReasonCode = reasonCode,
            intent,
            admitted,
            blockReason = admitted ? null : blockReason ?? DispositionBlockReason(evaluated)
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

    private static (string Evaluated, string ReasonCode, int? NextWaitMs, string? Intent) Classify(AgentDecision decision) =>
        decision switch
        {
            Speak speak => ("speak", "model_speak", speak.NextWaitMs, speak.Plan?.Intent),
            RequestDeactivate => ("deactivate", "model_deactivate", null, null),
            StaySilent silent when !silent.CountsTowardSilentCap && silent.Reason.Contains("provider", StringComparison.OrdinalIgnoreCase) =>
                ("staySilent", "provider_failed", silent.NextWaitMs, null),
            StaySilent silent when !silent.CountsTowardSilentCap && silent.Reason.Contains("parse", StringComparison.OrdinalIgnoreCase) =>
                ("staySilent", "unparseable", silent.NextWaitMs, null),
            StaySilent silent when !silent.CountsTowardSilentCap =>
                ("staySilent", "infrastructure", silent.NextWaitMs, null),
            StaySilent silent => ("staySilent", "semantic_silence", silent.NextWaitMs, null),
            _ => ("unknown", "unknown", null, null)
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
