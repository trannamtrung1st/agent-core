using AgentCore.Application.Context;
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
                        WithTools(context, builder, builder.Build(context, responseId, speak.Plan)),
                        NextWaitMs: speak.NextWaitMs,
                        Plan: speak.Plan),
                InitiativeParsedDecision.InvalidPlan =>
                    new StaySilent("Initiative plan was invalid.", CountsTowardSilentCap: false),
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

        var boundaryValid = SummaryBoundary.IsValid(
            context.Summary,
            context.SummarizedThroughEntrySequence,
            SummaryBoundary.ResolveLastSequence(context.LastEntrySequence, context.History),
            context.History);
        var recent = SummaryBoundary.SelectHistory(
                context.Summary,
                context.SummarizedThroughEntrySequence,
                context.LastEntrySequence,
                context.History)
            .Where(entry => entry.IsPromptTurn)
            .TakeLast(8)
            .Select(entry => new
            {
                role = entry.Role.ToString(),
                text = Clip(entry.Role == ConversationRole.Assistant
                    ? AssistantSemanticProjection.Text(entry)
                    : entry.Text, 320),
                status = entry.Status.ToString()
            });

        var identity = context.EffectiveIdentity;
        var agent = new
        {
            id = context.Definition.Id,
            name = identity.Name,
            role = identity.Role,
            tone = identity.Tone,
            goals = context.Definition.Goals,
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
            sessionSummary = SummaryBoundary.RememberedData(context.Summary, boundaryValid),
            recentTurns = recent,
            agent
        };

        var definitionRevision = $"{context.Definition.Id}:{context.Definition.Version}";
        var instructionSource = context.Configuration?.FieldSources.GetValueOrDefault("instructions.agentInstructions");
        return ContextAssembler.Create(Guid.Empty, new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract), ContextSection.Core(CoreProcedure.Initiative),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, context.Persona is null ? ContextSourceKind.Definition : ContextSourceKind.Instance,
                PromptContextBuilder.BuildAgentIdentity(context.Definition, identity), context.Persona is null ? definitionRevision : context.Configuration?.PersonaRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, instructionSource?.Kind == "Instance" ? ContextSourceKind.Instance : ContextSourceKind.Definition,
                context.Definition.AgentInstructions, instructionSource?.Revision ?? definitionRevision),
            ContextSection.Runtime(ContextSectionId.Policies, ContextSourceKind.AgentRun,
                PromptContextBuilder.BuildAgentPolicies(context.Definition) + "\nInitiative policy: " + JsonSerializer.Serialize(agent.initiativePolicy, Json), context.Configuration?.ConfigurationHash),
            ContextSection.Evidence(ContextSectionId.History, ContextSourceKind.Session, JsonSerializer.Serialize(payload, Json))
        ], fieldSources: context.Configuration?.FieldSources), maxOutputTokens: 160, temperature: 0.2, reasoningEffort: context.ReasoningEffort);
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
                "speak" => TryReadSpeakPlan(root, out var plan, out var speakWait) && plan is not null
                    ? new InitiativeParsedDecision.Speak(plan, speakWait)
                    : InitiativeParsedDecision.InvalidPlan.Instance,
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

    private static bool TryReadSpeakPlan(JsonElement root, out InitiativePlan? plan, out int? nextWaitMs)
    {
        plan = null;
        nextWaitMs = TryReadNextWaitMs(root);
        if (!root.TryGetProperty("intent", out var intentNode))
        {
            return false;
        }

        if (!root.TryGetProperty("objective", out var objectiveNode)
            || objectiveNode.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return InitiativePlan.TryCreate(intentNode.GetString(), objectiveNode.GetString(), out plan);
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

    private static ModelRequest WithTools(AgentContext context, PromptContextBuilder builder, ModelRequest request)
    {
        var tools = builder.OfferTools(context.Definition, context);
        return tools.Count == 0 ? request : request with { Tools = tools };
    }

    private abstract record InitiativeParsedDecision
    {
        public sealed record Speak(InitiativePlan Plan, int? NextWaitMs) : InitiativeParsedDecision;

        public sealed record Stay(string Reason, int? NextWaitMs) : InitiativeParsedDecision;

        public sealed record Deactivate(string Reason) : InitiativeParsedDecision;

        public sealed record InvalidPlan : InitiativeParsedDecision
        {
            public static InvalidPlan Instance { get; } = new();
        }
    }
}
