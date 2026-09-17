using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public static class InitiativeEvaluator
{
    public const string Marker = "initiative-decision-v2";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static ModelRequest CreateEvaluationRequest(AgentContext context, PromptContextBuilder _) =>
        BuildEvaluationRequest(context);

    public static async ValueTask<AgentDecision> EvaluateAsync(
        ILanguageModel languageModel,
        PromptContextBuilder builder,
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var request = BuildEvaluationRequest(context);
        var (text, providerFailed) = await CollectTextAsync(languageModel, request, cancellationToken)
            .ConfigureAwait(false);
        AgentDecision decision;
        if (providerFailed)
        {
            decision = new StaySilent("Initiative provider failed.", CountsTowardSilentCap: false);
        }
        else if (!TryParseDecision(text, out var parsed))
        {
            decision = new StaySilent("Initiative evaluation was not parseable.", CountsTowardSilentCap: false);
        }
        else
        {
            decision = parsed switch
            {
                InitiativeParsedDecision.Deactivate deactivate =>
                    new RequestDeactivate(deactivate.Reason),
                InitiativeParsedDecision.Stay stay =>
                    new StaySilent(stay.Reason, CountsTowardSilentCap: true, NextWaitMs: stay.NextWaitMs),
                InitiativeParsedDecision.Speak speak =>
                    new Speak(
                        WithTools(context, builder.Build(context, responseId, speak.Plan)),
                        Plan: speak.Plan),
                _ => new StaySilent("Initiative evaluation was empty.", CountsTowardSilentCap: false)
            };
        }

        InitiativeEvaluationTelemetry.RecordEvaluation(context, decision, RuntimeTelemetry.ElapsedMs(started));
        return decision;
    }

    private static ModelRequest BuildEvaluationRequest(AgentContext context)
    {
        var policy = context.Definition.InitiativePolicy;
        var lastUserAt = context.LastUserActivityAt ?? context.UtcNow;
        var silenceMs = Math.Max(0, (context.UtcNow - lastUserAt).TotalMilliseconds);
        var lastAssistantAt = context.History
            .Where(entry => entry.Role == ConversationRole.Assistant && entry.Status != EntryStatus.Streaming)
            .Select(entry => entry.CreatedAt)
            .LastOrDefault();
        var sinceAssistantMs = lastAssistantAt == default
            ? 0
            : Math.Max(0, (context.UtcNow - lastAssistantAt).TotalMilliseconds);

        var recent = context.History
            .TakeLast(8)
            .Select(entry => new
            {
                role = entry.Role.ToString(),
                text = Clip(entry.Role == ConversationRole.Assistant
                    ? PromptContextBuilder.EligibleAssistantText(entry)
                    : entry.Text, 320),
                status = entry.Status.ToString()
            });

        var agent = new
        {
            id = context.Definition.Id,
            name = context.Definition.Identity.Name,
            role = context.Definition.Identity.Role,
            tone = context.Definition.Identity.Tone,
            goals = context.Definition.Goals,
            systemInstructions = Clip(context.Definition.SystemInstructions, 600),
            conversationPolicy = new
            {
                responseLength = context.Definition.ConversationPolicy.ResponseLength,
                askOneQuestionAtATime = context.Definition.ConversationPolicy.AskOneQuestionAtATime,
                language = context.Definition.ConversationPolicy.Language
            },
            initiativePolicy = new
            {
                silenceThresholdMs = policy.SilenceThresholdMs,
                cooldownMs = policy.CooldownMs,
                maxPerSilencePeriod = policy.MaxPerSilencePeriod,
                consecutiveCap = policy.ConsecutiveCap,
                triggers = policy.Triggers
            }
        };

        var payload = new
        {
            trigger = context.Trigger.Kind.ToString(),
            triggerText = context.Trigger.Text,
            environmentKind = context.Trigger.EnvironmentKind,
            pendingTopic = context.PendingTopic,
            mode = context.Mode.ToString(),
            consecutiveProactiveSpeaks = context.ConsecutiveProactiveSpeaks,
            speaksThisSilencePeriod = context.SpeaksThisSilencePeriod,
            silentEvaluations = context.SilentEvaluations,
            consecutiveCap = policy.ConsecutiveCap,
            maxPerSilencePeriod = policy.MaxPerSilencePeriod,
            silenceMs,
            sinceAssistantMs,
            inactivityExceeded = context.InactivityExceeded,
            recentTurns = recent,
            agent
        };

        var agentContext = PromptContextBuilder.BuildInitiativeAgentContext(context.Definition);
        return new ModelRequest(
            Guid.Empty,
            [
                new ModelMessage(
                    ModelRole.System,
                    string.Join(
                        '\n',
                        Marker,
                        """
                        You decide whether a proactive agent message is worthwhile right now and, when speaking, what conversational move is useful.
                        Reply with a single JSON object only, no markdown.
                        staySilent/deactivate:
                        {"decision":"staySilent"|"deactivate","reason":"...","nextWaitMs":number|null}
                        speak (required fields for speak):
                        {"decision":"speak","intent":"hint"|"rephrase"|"clarification"|"reminder"|"followUp"|"other","objective":"short internal instruction for what the proactive message should accomplish","nextWaitMs":number|null}
                        Do not write the user-visible response text. objective is internal planning only.
                        Speak when a proactive turn would meaningfully advance the interaction. Choose intent for the move (hint, rephrase, etc.).
                        Stay silent when the message would mainly repeat readiness, encouragement, or previous wording without moving the interaction forward.
                        Reject empty check-ins such as "I'm here when you're ready" unless genuinely appropriate for the role.
                        Use deactivate only when the session should pause (rare).
                        nextWaitMs is optional milliseconds until the next initiative evaluation (staySilent/deactivate).
                        """,
                        "Agent context:",
                        agentContext)),
                new ModelMessage(ModelRole.User, JsonSerializer.Serialize(payload, Json))
            ],
            MaxOutputTokens: 160,
            Temperature: 0.2);
    }

    private static async Task<(string Text, bool ProviderFailed)> CollectTextAsync(
        ILanguageModel languageModel,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        await foreach (var evt in languageModel.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (evt is ModelFailed)
            {
                return (builder.ToString(), true);
            }

            if (evt is ModelTextDelta delta)
            {
                builder.Append(delta.Text);
            }
        }

        return (builder.ToString(), false);
    }

    private static bool TryParseDecision(string raw, out InitiativeParsedDecision? decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("decision", out var decisionNode)
                ? decisionNode.GetString()
                : null;
            var reason = root.TryGetProperty("reason", out var reasonNode)
                ? reasonNode.GetString() ?? "Initiative evaluation."
                : "Initiative evaluation.";
            var nextWait = TryReadNextWaitMs(root);

            decision = kind switch
            {
                "speak" when TryReadSpeakPlan(root, reason, out var plan) =>
                    new InitiativeParsedDecision.Speak(plan),
                "deactivate" => new InitiativeParsedDecision.Deactivate(reason),
                "staySilent" or "stay_silent" => new InitiativeParsedDecision.Stay(reason, nextWait),
                _ => null
            };
            return decision is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadSpeakPlan(JsonElement root, string fallbackReason, out InitiativePlan plan)
    {
        var objective = root.TryGetProperty("objective", out var objectiveNode)
            ? objectiveNode.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            objective = string.IsNullOrWhiteSpace(fallbackReason)
                ? "Advance the interaction with one useful proactive turn."
                : fallbackReason.Trim();
        }

        var intent = root.TryGetProperty("intent", out var intentNode)
            ? InitiativeIntents.Normalize(intentNode.GetString())
            : InitiativeIntents.Other;
        plan = new InitiativePlan(intent, objective);
        return true;
    }

    private static int? TryReadNextWaitMs(JsonElement root)
    {
        if (!root.TryGetProperty("nextWaitMs", out var waitNode) || waitNode.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (waitNode.TryGetInt32(out var value))
        {
            return value < 0 ? null : Math.Min(value, 3_600_000);
        }

        if (waitNode.TryGetInt64(out var wide) && wide >= 0 && wide <= int.MaxValue)
        {
            return (int)wide;
        }

        if (waitNode.TryGetDouble(out var fractional) && fractional >= 0 && fractional <= int.MaxValue)
        {
            return (int)Math.Round(fractional);
        }

        return null;
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string Clip(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max];
    }

    private static ModelRequest WithTools(AgentContext context, ModelRequest request)
    {
        var tools = ToolCatalog.For(context.Definition, context);
        return tools.Count == 0 ? request : request with { Tools = tools };
    }

    private abstract record InitiativeParsedDecision
    {
        public sealed record Speak(InitiativePlan Plan) : InitiativeParsedDecision;

        public sealed record Stay(string Reason, int? NextWaitMs) : InitiativeParsedDecision;

        public sealed record Deactivate(string Reason) : InitiativeParsedDecision;
    }
}
