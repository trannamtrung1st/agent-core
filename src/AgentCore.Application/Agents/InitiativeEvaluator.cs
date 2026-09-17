using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public static class InitiativeEvaluator
{
    public const string Marker = "initiative-decision-v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async ValueTask<AgentDecision> EvaluateAsync(
        ILanguageModel languageModel,
        PromptContextBuilder builder,
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        var request = BuildEvaluationRequest(context);
        var text = await CollectTextAsync(languageModel, request, cancellationToken).ConfigureAwait(false);
        if (!TryParseDecision(text, out var parsed))
        {
            return new StaySilent("Initiative evaluation was not parseable.", CountsTowardSilentCap: true);
        }

        return parsed switch
        {
            InitiativeParsedDecision.Deactivate deactivate =>
                new RequestDeactivate(deactivate.Reason),
            InitiativeParsedDecision.Stay stay =>
                new StaySilent(stay.Reason, CountsTowardSilentCap: true, NextWaitMs: stay.NextWaitMs),
            InitiativeParsedDecision.Speak =>
                new Speak(WithTools(context, builder.Build(context, responseId))),
            _ => new StaySilent("Initiative evaluation was empty.", CountsTowardSilentCap: true)
        };
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
            .TakeLast(6)
            .Select(entry => new
            {
                role = entry.Role.ToString(),
                text = Clip(entry.Role == ConversationRole.Assistant
                    ? PromptContextBuilder.EligibleAssistantText(entry)
                    : entry.Text, 240),
                status = entry.Status.ToString()
            });

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
            recentTurns = recent
        };

        return new ModelRequest(
            Guid.Empty,
            [
                new ModelMessage(
                    ModelRole.System,
                    """
                    initiative-decision-v1
                    You decide whether a proactive agent message is worthwhile right now.
                    Reply with a single JSON object only, no markdown:
                    {"decision":"speak"|"staySilent"|"deactivate","reason":"...","nextWaitMs":number|null}
                    Use speak only when you can add concrete value (new help, a specific follow-up, or advancing an open task).
                    Use staySilent when repeating encouragement, readiness, or prior wording would be the main content.
                    Use deactivate only when the session should pause (rare).
                    nextWaitMs is optional milliseconds until the next initiative evaluation (staySilent/deactivate).
                    """),
                new ModelMessage(ModelRole.User, JsonSerializer.Serialize(payload, Json))
            ],
            MaxOutputTokens: 120,
            Temperature: 0.2);
    }

    private static async Task<string> CollectTextAsync(
        ILanguageModel languageModel,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        await foreach (var evt in languageModel.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (evt is ModelTextDelta delta)
            {
                builder.Append(delta.Text);
            }
        }

        return builder.ToString();
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
                "speak" => InitiativeParsedDecision.Speak.Instance,
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
        public sealed record Speak : InitiativeParsedDecision
        {
            public static Speak Instance { get; } = new();
        }

        public sealed record Stay(string Reason, int? NextWaitMs) : InitiativeParsedDecision;

        public sealed record Deactivate(string Reason) : InitiativeParsedDecision;
    }
}
