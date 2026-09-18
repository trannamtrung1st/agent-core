using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Agents;

public static class CompletionEvaluator
{
    public const string Marker = "completion-decision-v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static bool ShouldEvaluate(SessionSnapshot snapshot)
    {
        var purpose = snapshot.Purpose ?? SessionPurpose.OngoingDefault;
        var policy = snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default;
        return purpose.Kind == SessionPurposeKind.Goal
            && policy.AgentCompletion != AgentCompletionAuthority.Disabled
            && !SessionLifecycle.IsTerminal(snapshot.LifecycleStatus)
            && snapshot.Status is not (SessionStatus.Ended or SessionStatus.Ending or SessionStatus.Paused);
    }

    public static ModelRequest CreateEvaluationRequest(SessionSnapshot snapshot, DateTimeOffset now) =>
        BuildEvaluationRequest(snapshot, now);

    public static async ValueTask<CompletionDecision> EvaluateAsync(
        ILanguageModel languageModel,
        SessionSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var request = BuildEvaluationRequest(snapshot, now);
        var (text, providerFailed) = await CollectTextAsync(languageModel, request, cancellationToken)
            .ConfigureAwait(false);
        CompletionDecision decision;
        if (providerFailed)
        {
            decision = new ContinueSession("Completion provider failed.");
        }
        else if (!TryParseDecision(text, out var parsed) || parsed is null)
        {
            decision = new ContinueSession("Completion evaluation was not parseable.");
        }
        else
        {
            decision = parsed;
        }

        RuntimeTelemetry.Record(
            "completion_eval",
            RuntimeTelemetry.ElapsedMs(started),
            decision is RequestComplete ? "requestComplete" : "continue");
        return decision;
    }

    private static ModelRequest BuildEvaluationRequest(SessionSnapshot snapshot, DateTimeOffset now)
    {
        var purpose = snapshot.Purpose ?? SessionPurpose.OngoingDefault;
        var policy = snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default;
        var recent = snapshot.Entries
            .TakeLast(8)
            .Select(entry => new
            {
                role = entry.Role.ToString(),
                text = Clip(
                    entry.Role == ConversationRole.Assistant
                        ? PromptContextBuilder.EligibleAssistantText(entry)
                        : entry.Text,
                    320),
                status = entry.Status.ToString()
            });

        var payload = new
        {
            purposeKind = purpose.Kind.ToString(),
            purposeDescription = purpose.Description,
            agentCompletion = policy.AgentCompletion.ToString(),
            mode = snapshot.Mode.ToString(),
            recentTurns = recent,
            utcNow = now
        };

        return new ModelRequest(
            Guid.Empty,
            [
                new ModelMessage(
                    ModelRole.System,
                    string.Join(
                        '\n',
                        Marker,
                        """
                        You decide whether a goal-oriented session should request completion after a finished assistant turn.
                        Reply with a single JSON object only, no markdown.
                        continue:
                        {"decision":"continue","reason":"..."}
                        requestComplete:
                        {"decision":"requestComplete","reason":"..."}
                        Do not write user-visible response text. continue when the goal is still open.
                        requestComplete only when the current conversation has met the stated goal.
                        Domain-specific exam, interview, or support pass/fail rules are not yours to invent.
                        """)),
                new ModelMessage(ModelRole.User, JsonSerializer.Serialize(payload, Json))
            ],
            MaxOutputTokens: 80,
            Temperature: 0);
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

    private static bool TryParseDecision(string raw, out CompletionDecision? decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("decision", out var decisionNode)
                ? decisionNode.GetString()
                : null;
            var reason = root.TryGetProperty("reason", out var reasonNode)
                ? reasonNode.GetString() ?? "Completion evaluation."
                : "Completion evaluation.";
            decision = kind switch
            {
                "continue" => new ContinueSession(reason),
                "requestComplete" or "request_complete" => new RequestComplete(reason),
                _ => null
            };
            return decision is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Clip(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max];
    }
}
