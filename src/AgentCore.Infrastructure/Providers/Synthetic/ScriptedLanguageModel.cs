using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public enum CompactionFixture
{
    Echo,
    Empty,
    Malformed,
    Oversized,
    Throw,
    Late
}

public sealed class ScriptedLanguageModel : ILanguageModel
{
    private readonly IReadOnlyList<string> _chunks;
    private readonly TaskCompletionSource? _release;
    private readonly bool _emitAfterCancel;
    private readonly bool _alwaysToolCall;
    private readonly string? _completionDecision;
    private readonly bool _completionProviderFailed;
    private readonly CompactionFixture _compactionFixture;
    private readonly TaskCompletionSource? _compactionRelease;
    private readonly TaskCompletionSource? _compactionStarted;
    private readonly Queue<IReadOnlyList<MemoryProposal>> _memoryTurns;
    private readonly ConcurrentDictionary<Guid, ScheduleScratch> _scheduleScratchByKey = new();
    private static readonly ITriggerCommandAuthorizer ScheduleAuthorizer = new HeuristicTriggerCommandAuthorizer();
    private const string DiagnosticFailureMarker = "synthetic-fail-turn";

    private sealed class ScheduleScratch
    {
        public string? RegistrationId { get; set; }

        public long Revision { get; set; }

        public string? Intent { get; set; }

        public string? TimeZone { get; set; }
    }

    public ScriptedLanguageModel(
        IReadOnlyList<string>? chunks = null,
        TaskCompletionSource? releaseAfterFirstChunk = null,
        bool emitAfterCancel = false,
        bool alwaysToolCall = false,
        string? completionDecision = null,
        bool completionProviderFailed = false,
        CompactionFixture compactionFixture = CompactionFixture.Echo,
        TaskCompletionSource? compactionRelease = null,
        TaskCompletionSource? compactionStarted = null,
        IReadOnlyList<IReadOnlyList<MemoryProposal>>? memoryTurns = null)
    {
        _chunks = chunks ?? DefaultChunks;
        _release = releaseAfterFirstChunk;
        _emitAfterCancel = emitAfterCancel;
        _alwaysToolCall = alwaysToolCall;
        _completionDecision = completionDecision;
        _completionProviderFailed = completionProviderFailed;
        _compactionFixture = compactionFixture;
        _compactionRelease = compactionRelease;
        _compactionStarted = compactionStarted;
        _memoryTurns = new Queue<IReadOnlyList<MemoryProposal>>(memoryTurns ?? []);
    }

    public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

    public static IReadOnlyList<string> DefaultChunks { get; } = ["Hello", " from ", "synthetic."];

    public static IReadOnlyList<string> LongerChunks { get; } =
        ["There are three points. ", "First, stay present. ", "Second, listen. ", "Third, answer briefly."];

    public static IReadOnlyList<string> ShortChunks { get; } = ["OK."];

    public static IReadOnlyList<string> MarkdownChunks { get; } =
        [
            "The architecture has **three** pieces:\n\n",
            "1. Session runtime\n2. Agent execution\n\n---\n\n",
            "Use `IAgentProvider`.\n"
        ];

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.ToolChoiceName == AgentCore.Application.Experience.ExperienceService.RecordTool)
        {
            var source = request.Messages.Last(m => m.Role == ModelRole.User).Text;
            var content = new AgentCore.Domain.Experience.ExperienceContent("Review observable completed work",
                ["Worked through the recorded user request"], [], ["Completed observable source checkpoint"],
                source.Contains("correction", StringComparison.OrdinalIgnoreCase) ? ["The user supplied a correction"] : [],
                [], source.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ["A recorded approach failed"] : [],
                ["Verify observable state before acting"]);
            yield return new ModelToolCallEvent(new("experience-result", AgentCore.Application.Experience.ExperienceService.RecordTool,
                JsonSerializer.Serialize(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }
        if (IdentityMaintenanceScript.Generate(request) is { } maintenanceEvents)
        {
            foreach (var item in maintenanceEvents) yield return item;
            yield break;
        }
        if (ThoughtActivationScript.Generate(request) is { } thoughtEvents)
        {
            foreach (var item in thoughtEvents) yield return item;
            yield break;
        }
        if (request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text == "Use my recent experience before acting."
            && request.Messages.Any(m => (m.Text.StartsWith("Historical Experience", StringComparison.Ordinal) || m.Text.StartsWith("Historical Continuity", StringComparison.Ordinal) && m.Text.Contains("\"kind\":\"Experience\"", StringComparison.Ordinal))))
        {
            yield return new ModelTextDelta("I will observe current page state before acting, based on earlier experience. Current policy still controls every action.");
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }
        if (Offers(request, ToolCatalog.WorkComplete)
            && IsScheduledReminderDelivery(request, request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "", out var reminder))
        {
            yield return new ModelToolCallEvent(new("reminder-complete", ToolCatalog.WorkComplete,
                JsonSerializer.Serialize(new { summary = reminder, attentionRequired = false })));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }
        if (AgentWorkspaceScript.Generate(request) is { } workspaceEvents)
        {
            foreach (var item in workspaceEvents) yield return item;
            yield break;
        }
        if (HarnessChatScript.Generate(request) is { } chatEvents)
        {
            foreach (var item in chatEvents) yield return item;
            yield break;
        }

        if (IsCompactionRequest(request))
        {
            await foreach (var item in GenerateCompactionAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        if (TryInitiativeDecision(request, out var initiativeJson))
        {
            yield return new ModelTextDelta(initiativeJson);
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (TryCompletionDecision(request, out var completionJson, out var completionFailed))
        {
            if (completionFailed)
            {
                yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Synthetic completion failure."));
                yield break;
            }

            yield return new ModelTextDelta(completionJson);
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        var diagnosticUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        if (diagnosticUser.Contains(DiagnosticFailureMarker, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Synthetic turn failure."));
            yield break;
        }

        if (TryScriptMessagingSkillJourney(request, out var journeyEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var journeyEvent in journeyEvents)
            {
                yield return journeyEvent;
            }

            yield break;
        }

        if (TryScriptBrowserSignup(request, out var signupEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var signupEvent in signupEvents)
            {
                yield return signupEvent;
            }

            yield break;
        }

        if (TryScriptBrowserChallenge(request, out var challengeEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var challengeEvent in challengeEvents)
            {
                yield return challengeEvent;
            }

            yield break;
        }

        if (TryScriptBrowserTargetDenied(request, out var denialEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var denialEvent in denialEvents)
            {
                yield return denialEvent;
            }

            yield break;
        }

        if (TryScriptProductPublish(request, out var productEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var productEvent in productEvents)
            {
                yield return productEvent;
            }

            yield break;
        }

        if (TryScriptBrowserRecordLookup(request, out var browserEvents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var browserEvent in browserEvents)
            {
                yield return browserEvent;
            }

            yield break;
        }

        if (HasSessionTools(request) && (_alwaysToolCall || ShouldScriptTools(request)))
        {
            await foreach (var item in GenerateToolScriptAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var chunks = Select(request, lastUser);
        var proposals = TakeMemoryTurn();
        if (lastUser.StartsWith("synthetic-inferred-frontend:", StringComparison.Ordinal))
        {
            var subject = lastUser.Split(':', 2)[1].Trim();
            proposals = [new MemoryProposal(MemoryProposalOperation.Upsert, AgentCore.Domain.Memory.MemoryKind.Preference,
                "Frontend " + subject, "Prefer TypeScript for frontend examples.", MemoryScopeHint.IdentityUser, MemoryProposalSource.AgentInferred)];
            chunks = ["Observed a durable frontend preference."];
        }
        if (RequestsNativeJson(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            yield return new ModelTextDelta(ToNativeJson(lastUser, chunks, proposals));
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (proposals.Count > 0)
        {
            chunks = [..chunks, MemoryProposalCodec.Marker(proposals)];
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            if (!_emitAfterCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (index == 1 && _release is not null)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (index == 0 && lastUser.Contains("markdown", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            yield return new ModelTextDelta(chunks[index]);
            if (index == 0 && lastUser.Contains(SteerProbeMarker, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            }

            if (index == 0 && lastUser.Contains(DurableStreamLongHoldMarker, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(45_000, cancellationToken).ConfigureAwait(false);
            }
            else if (index == 0 && lastUser.Contains(DurableStreamProbeMarker, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
            }

            if (index == 0 && lastUser.Contains("hold the line", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (_release is not null && index == 0 && chunks.Count == 1)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateToolScriptAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolRounds = request.Messages.Count(message => message.Role == ModelRole.Tool);
        var scheduleToolRounds = ToolRoundsSinceLastUser(request.Messages);
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var lastTool = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool)?.Text ?? string.Empty;
        var scheduleLastTool = LastToolTextSinceLastUser(request.Messages);
        var lastToolMessage = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool);
        if (lastToolMessage?.Parts?.OfType<ModelImageContent>().Any() == true)
        {
            yield return new ModelTextDelta(HistoricalImageRereadAnswer);
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (TryScriptDefinitionEvaluation(request, toolRounds, out var definitionEvaluationEvents))
        {
            foreach (var definitionEvaluationEvent in definitionEvaluationEvents)
            {
                yield return definitionEvaluationEvent;
            }

            yield break;
        }

        if (_alwaysToolCall)
        {
            yield return new ModelToolCallEvent(new ModelToolCall($"call-{toolRounds + 1}", ToolCatalog.KnowledgeRetrieve, """{"identity":"support-order-policy"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (TryScriptEmailHarness(request, toolRounds, lastUser, lastTool, out var emailEvent))
        {
            yield return emailEvent;
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 4
            && lastUser.Contains(EmailHarnessMarker, StringComparison.OrdinalIgnoreCase)
            && lastTool.Contains("\"outcome\":\"sent\"", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ModelTextDelta("Email harness completed after approval.");
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (TryScriptSchedule(request, scheduleToolRounds, lastUser, scheduleLastTool, out var scheduleEvent))
        {
            if (scheduleEvent is null)
            {
                yield return new ModelTextDelta(ScheduleFinalText(lastUser, lastTool));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return scheduleEvent;
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 0
            && lastUser.Contains(SensitiveApprovalMarker, StringComparison.OrdinalIgnoreCase)
            && Offers(request, ToolCatalog.DemoSensitiveAction))
        {
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-sensitive",
                ToolCatalog.DemoSensitiveAction,
                """{"label":"Synthetic sensitive approval"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 1
            && lastUser.Contains(SensitiveApprovalMarker, StringComparison.OrdinalIgnoreCase)
            && lastTool.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            if (Offers(request, ToolCatalog.WorkComplete))
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "call-sensitive-complete",
                    ToolCatalog.WorkComplete,
                    """{"summary":"Sensitive action completed after approval.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelTextDelta("Sensitive action completed after approval.");
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (toolRounds == 0
            && lastUser.Contains(HistoricalImageRereadMarker, StringComparison.OrdinalIgnoreCase)
            && Offers(request, ToolCatalog.AttachmentsRead)
            && TryReadManifestAttachmentId(request, out var historicalAttachmentId))
        {
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-historical-image",
                ToolCatalog.AttachmentsRead,
                $$"""{"attachmentId":"{{historicalAttachmentId:D}}"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 0)
        {
            var identity = lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
                || lastUser.Contains("compliance", StringComparison.OrdinalIgnoreCase)
                ? "compliance-retention"
                : "support-order-policy";
            var name = Offers(request, ToolCatalog.KnowledgeRetrieve) ? ToolCatalog.KnowledgeRetrieve : request.Tools![0].Name;
            yield return new ModelToolCallEvent(new ModelToolCall("call-1", name, $"{{\"identity\":\"{identity}\"}}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 1 && Offers(request, ToolCatalog.ArtifactsCreate))
        {
            var summary = BuildArtifactSummary(lastUser, lastTool);
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-2",
                ToolCatalog.ArtifactsCreate,
                $"{{\"displayName\":\"case-note.md\",\"contentType\":\"text/markdown\",\"content\":\"{EscapeJson(summary)}\"}}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (_release is not null)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var artifactId = ExtractArtifactId(lastTool);
        var body = BuildFinalAnswer(lastUser, request.Messages, artifactId);
        yield return new ModelTextDelta(body);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private static string BuildArtifactSummary(string lastUser, string lastTool)
    {
        if (TryReadKnowledgeTool(lastTool, out var knowledge))
        {
            return SummarizeKnowledgeContent(knowledge.Content);
        }

        return lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            ? "Retention applies to the current demonstration session."
            : "Order 91 is delayed under the simulated policy.";
    }

    private static string SummarizeKnowledgeContent(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            return trimmed.TrimEnd('.');
        }

        return content.Trim();
    }

    private static string BuildFinalAnswer(
        string lastUser,
        IReadOnlyList<ModelMessage> messages,
        string artifactId)
    {
        var knowledgeJson = messages
            .LastOrDefault(message => message.Role == ModelRole.Tool && TryReadKnowledgeTool(message.Text, out _))
            ?.Text;
        if (knowledgeJson is not null && TryReadKnowledgeTool(knowledgeJson, out var document))
        {
            if (document.Identity.Contains("compliance-retention", StringComparison.Ordinal))
            {
                return
                    $"Transcripts are kept for the current demonstration session. [[md:**demonstration session**]] Cite {document.Citation}. [[artifact:{artifactId}]]";
            }

            return $"Order 91 is delayed. [[md:**Delayed**]] [[artifact:{artifactId}]]";
        }

        return lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            ? $"Transcripts are kept for the current demonstration session. [[artifact:{artifactId}]]"
            : $"Order 91 is delayed. [[md:**Delayed**]] [[artifact:{artifactId}]]";
    }

    private static bool TryReadKnowledgeTool(string toolJson, out KnowledgeToolPayload document)
    {
        document = default!;
        if (string.IsNullOrWhiteSpace(toolJson))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(toolJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("identity", out var identity)
                || !root.TryGetProperty("citation", out var citation)
                || !root.TryGetProperty("content", out var content))
            {
                return false;
            }

            document = new KnowledgeToolPayload(
                identity.GetString() ?? string.Empty,
                citation.GetString() ?? string.Empty,
                content.GetString() ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record KnowledgeToolPayload(string Identity, string Citation, string Content);

    private bool TryScriptSchedule(
        ModelRequest request,
        int toolRounds,
        string lastUser,
        string lastTool,
        out ModelGenerationEvent? toolEvent)
    {
        toolEvent = null;
        if (!Offers(request, ToolCatalog.TriggerScheduleOnce)
            && !Offers(request, ToolCatalog.TriggerList)
            && !Offers(request, ToolCatalog.TriggerScheduleRecurring))
        {
            return false;
        }

        var scheduleTurn = IsScheduleTurn(request, lastUser);
        if (!scheduleTurn)
        {
            return false;
        }

        if (toolRounds > 0)
        {
            RememberSchedule(request, lastTool);
            if (lastTool.Contains("\"error\"", StringComparison.Ordinal))
            {
                return true;
            }

            if (toolRounds == 1 && lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase))
            {
                toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerList, "{}");
                return true;
            }

            if (toolRounds == 2 && lastUser.Contains("move that schedule", StringComparison.OrdinalIgnoreCase))
            {
                toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerUpdate, ScheduleMutationArgs(lastTool, "11:00", includeTime: true));
                return true;
            }

            if (toolRounds == 3 && lastUser.Contains("cancel that schedule", StringComparison.OrdinalIgnoreCase))
            {
                toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerCancel, ScheduleMutationArgs(lastTool, "11:00", includeTime: false));
                return true;
            }

            return true;
        }

        if (lastUser.Contains("every monday", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleRecurring,
                """{"intent":"Weekly call","kind":"weekly","interval":1,"weekdays":["monday"],"localTime":"09:00"}""");
            return true;
        }

        if (lastUser.Contains(ScheduleForceMarker, StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerScheduleOnce, """{"intent":"Sneaky","relativeDayOffset":1,"localTime":"09:00"}""");
            return true;
        }

        if (lastUser.Contains("remind me", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("set a reminder", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerScheduleOnce, """{"intent":"Call John","relativeDayOffset":1,"localTime":"09:00"}""");
            return true;
        }

        if (lastUser.Contains("check the oven", StringComparison.OrdinalIgnoreCase)
            && lastUser.Contains("minute", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleOnce,
                """{"intent":"check the oven","relativeDelaySeconds":60}""");
            return true;
        }

        if (lastUser.Contains("say hello to me", StringComparison.OrdinalIgnoreCase)
            && lastUser.Contains("30", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleRecurring,
                """{"intent":"Say hello to me","kind":"fixed_interval","intervalSeconds":30}""");
            return true;
        }

        if (lastUser.Equals("every minute", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleRecurring,
                """{"kind":"fixed_interval","intervalSeconds":60}""");
            return true;
        }

        if (lastUser.Contains("hello", StringComparison.OrdinalIgnoreCase)
            && lastUser.Contains("min", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleOnce,
                """{"intent":"Hello","relativeDelaySeconds":60}""");
            return true;
        }

        if (lastUser.Contains("viet nam", StringComparison.OrdinalIgnoreCase)
            && lastUser.Contains("00:19", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleOnce,
                """{"intent":"Hello","localDate":"2026-09-24","localTime":"00:19","timeZone":"viet nam time"}""");
            return true;
        }

        if ((lastUser.Contains("viet nam", StringComparison.OrdinalIgnoreCase)
                || lastUser.Contains("vietnam", StringComparison.OrdinalIgnoreCase))
            && lastUser.Contains("8:49", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleOnce,
                """{"intent":"Hello","localDate":"2026-09-24","localTime":"08:49","timeZone":"viet nam time"}""");
            return true;
        }

        if (lastUser.Contains("another", StringComparison.OrdinalIgnoreCase)
            && lastUser.Contains("8:52", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerScheduleOnce,
                """{"intent":"Hello","localDate":"2026-09-24","localTime":"08:52","timeZone":"viet nam time"}""");
            return true;
        }

        if (ScheduleAuthorizer.IsScheduleConfirmation(lastUser, null))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerScheduleOnce, """{"intent":"Different","relativeDayOffset":2,"localTime":"15:00"}""");
            return true;
        }

        if (lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("what reminders", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("show my reminders", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerList, "{}");
            return true;
        }

        if (lastUser.Contains("move that", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("move that reminder", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("reschedule", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetScheduleScratch(request, out var moveScratch))
            {
                return false;
            }

            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerUpdate,
                ScheduleRememberedArgs(moveScratch, includeTime: true, ClockFromMove(lastUser)));
            return true;
        }

        if (lastUser.Contains("cancel that", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("cancel that reminder", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetScheduleScratch(request, out var cancelScratch))
            {
                return false;
            }

            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerCancel,
                ScheduleRememberedArgs(cancelScratch, includeTime: false, "10:00"));
            return true;
        }

        return false;
    }

    private bool IsScheduleTurn(ModelRequest request, string lastUser) =>
        lastUser.Contains(ScheduleForceMarker, StringComparison.OrdinalIgnoreCase)
        || TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(lastUser, null, SyntheticScheduleContext(request))
        || ScheduleAuthorizer.IsScheduleConfirmation(lastUser, null);

    private ScheduleConversationContext? SyntheticScheduleContext(ModelRequest request)
    {
        if (!TryGetScheduleScratch(request, out var scratch)
            || scratch.RegistrationId is null
            || !Guid.TryParse(scratch.RegistrationId, out var registrationId))
        {
            return null;
        }

        return new ScheduleConversationContext(
            registrationId,
            scratch.Revision,
            TriggerCommandAction.Create,
            scratch.Intent ?? "Call John",
            scratch.TimeZone ?? "UTC",
            TriggerScheduleKind.OneShot,
            TriggerRegistrationStatus.Active,
            null);
    }

    private bool TryGetScheduleScratch(ModelRequest request, out ScheduleScratch scratch)
    {
        scratch = null!;
        if (!TryResolveScheduleKey(request.Messages, out var key)
            || !_scheduleScratchByKey.TryGetValue(key, out var stored))
        {
            return false;
        }

        scratch = stored;
        return !string.IsNullOrWhiteSpace(scratch.RegistrationId);
    }

    private void RememberSchedule(ModelRequest request, string lastTool)
    {
        var remembered = ScheduleConversationContext.TryFromRegistrationJson(lastTool, TriggerCommandAction.Create);
        if (remembered is null)
        {
            return;
        }

        var scratch = new ScheduleScratch
        {
            RegistrationId = remembered.RegistrationId.ToString("D"),
            Revision = remembered.Revision,
            Intent = remembered.Intent,
            TimeZone = remembered.TimeZoneId
        };
        _scheduleScratchByKey[remembered.RegistrationId] = scratch;
        if (TryResolveScheduleKey(request.Messages, out var conversationKey)
            && conversationKey != remembered.RegistrationId)
        {
            _scheduleScratchByKey[conversationKey] = scratch;
        }
    }

    private static bool TryResolveScheduleKey(IReadOnlyList<ModelMessage> messages, out Guid key)
    {
        if (TryParseReferentRegistrationId(messages, out key))
        {
            return true;
        }

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != ModelRole.Tool)
            {
                continue;
            }

            var remembered = ScheduleConversationContext.TryFromRegistrationJson(
                message.Text,
                TriggerCommandAction.Create);
            if (remembered is not null && remembered.RegistrationId != Guid.Empty)
            {
                key = remembered.RegistrationId;
                return true;
            }
        }

        key = ConversationFingerprintKey(messages);
        return key != Guid.Empty;
    }

    private static bool TryParseReferentRegistrationId(IReadOnlyList<ModelMessage> messages, out Guid registrationId)
    {
        registrationId = Guid.Empty;
        foreach (var message in messages)
        {
            if (message.Role != ModelRole.System)
            {
                continue;
            }

            foreach (var line in message.Text.Split('\n'))
            {
                const string prefix = "registrationId=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (Guid.TryParse(line[prefix.Length..], out registrationId) && registrationId != Guid.Empty)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Guid ConversationFingerprintKey(IReadOnlyList<ModelMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append((int)message.Role).Append(':').Append(message.Text).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static string ScheduleRememberedArgs(ScheduleScratch scratch, bool includeTime, string localTime)
    {
        var intent = scratch.Intent ?? "Call John";
        if (!includeTime)
        {
            return $$"""{"registrationId":"{{scratch.RegistrationId}}","expectedRevision":{{scratch.Revision}}}""";
        }

        if (string.Equals(intent, "Call John", StringComparison.Ordinal))
        {
            return $$"""{"registrationId":"{{scratch.RegistrationId}}","expectedRevision":{{scratch.Revision}},"intent":"Call John","relativeDayOffset":1,"localTime":"{{localTime}}"}""";
        }

        var zone = string.IsNullOrWhiteSpace(scratch.TimeZone) ? "viet nam time" : scratch.TimeZone;
        return $$"""{"registrationId":"{{scratch.RegistrationId}}","expectedRevision":{{scratch.Revision}},"intent":"{{intent}}","localDate":"2026-09-24","localTime":"{{localTime}}","timeZone":"{{zone}}"}""";
    }

    private static string ClockFromMove(string text)
    {
        if (text.Contains("8:53", StringComparison.OrdinalIgnoreCase))
        {
            return "08:53";
        }

        if (text.Contains("8:52", StringComparison.OrdinalIgnoreCase))
        {
            return "08:52";
        }

        return text.Contains("to 11", StringComparison.OrdinalIgnoreCase) ? "11:00" : "10:00";
    }

    private static ModelToolCallEvent ScheduleCall(int toolRounds, string name, string arguments) =>
        new(new ModelToolCall($"call-schedule-{toolRounds + 1}", name, arguments));

    private static string ScheduleMutationArgs(string lastTool, string localTime, bool includeTime)
    {
        using var document = JsonDocument.Parse(lastTool);
        var root = document.RootElement;
        if (root.TryGetProperty("registrations", out var rows))
        {
            root = rows[0];
        }

        var id = root.GetProperty("registrationId").GetString();
        var revision = root.GetProperty("revision").GetInt64();
        return includeTime
            ? $$"""{"registrationId":"{{id}}","expectedRevision":{{revision}},"intent":"Call John","relativeDayOffset":1,"localTime":"{{localTime}}"}"""
            : $$"""{"registrationId":"{{id}}","expectedRevision":{{revision}}}""";
    }

    private static string ScheduleFinalText(string lastUser, string lastTool)
    {
        if (lastTool.Contains("confirmation_required", StringComparison.Ordinal))
        {
            return "I need you to confirm before I save that.";
        }

        if (lastTool.Contains("authorization_denied", StringComparison.Ordinal)
            || lastTool.Contains("authorization_ambiguous", StringComparison.Ordinal))
        {
            return "I did not understand that schedule request.";
        }

        if (lastTool.Contains("\"error\"", StringComparison.Ordinal))
        {
            return "I did not save a schedule.";
        }

        if (lastTool.Contains("Cancelled", StringComparison.Ordinal))
        {
            return "The schedule was cancelled.";
        }

        if (lastUser.Contains("what reminders", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("list schedules", StringComparison.OrdinalIgnoreCase))
        {
            return "You have a reminder: Call John.";
        }

        if (lastUser.Contains("move that reminder", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("move that schedule", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("reschedule", StringComparison.OrdinalIgnoreCase))
        {
            return "Moved the reminder.";
        }

        return lastUser.Contains("every monday", StringComparison.OrdinalIgnoreCase)
            ? "Scheduled the Monday call."
            : "Scheduled Call John.";
    }

    private static bool HasSessionTools(ModelRequest request) =>
        request.Tools?.Any(tool =>
            tool.Name != AssistantResponseSchema.ResponseFunctionName
            && tool.Name != ToolCatalog.AppMessageSend
            && tool.Name != ToolCatalog.SkillsLoad) == true;

    private bool ShouldScriptTools(ModelRequest request)
    {
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        return lastUser.Contains("support case", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("order 91", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("compliance case", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(HistoricalImageRereadMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(SensitiveApprovalMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(EmailHarnessMarker, StringComparison.OrdinalIgnoreCase)
            || IsScheduleTurn(request, lastUser)
            || request.Messages.Any(message => message.Role == ModelRole.Tool)
            || request.Messages.Any(message =>
                message.Role == ModelRole.System
                && message.Text.StartsWith(DefinitionEvaluationHarness.SystemPrefix, StringComparison.Ordinal));
    }

    private static bool TryScriptDefinitionEvaluation(
        ModelRequest request,
        int toolRounds,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        if (!TryReadDefinitionEvaluationDirective(request, out var checkType, out var toolName))
        {
            return false;
        }

        if (toolRounds > 0)
        {
            events =
            [
                new ModelTextDelta("Definition evaluation completed."),
                new ModelCompleted(ModelStopReason.Completed)
            ];
            return true;
        }

        switch (checkType)
        {
            case DefinitionEvaluationCheckType.TriggerSchedulePermitted:
                events =
                [
                    new ModelTextDelta("Trigger scheduling permitted for this draft."),
                    new ModelCompleted(ModelStopReason.Completed)
                ];
                return true;
            case DefinitionEvaluationCheckType.ResourceBound:
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return false;
                }

                events =
                [
                    new ModelToolCallEvent(new ModelToolCall(
                        "call-definition-eval-resource",
                        ToolCatalog.KnowledgeRetrieve,
                        $$"""{"identity":"{{toolName}}"}""")),
                    new ModelCompleted(ModelStopReason.ToolCalls)
                ];
                return true;
            case DefinitionEvaluationCheckType.ToolOffered:
            case DefinitionEvaluationCheckType.ToolNotOffered:
            case DefinitionEvaluationCheckType.ExternalActionDenied:
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return false;
                }

                events =
                [
                    new ModelToolCallEvent(new ModelToolCall(
                        "call-definition-eval-tool",
                        toolName,
                        """{"synthetic":"definition-evaluation"}""")),
                    new ModelCompleted(ModelStopReason.ToolCalls)
                ];
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadDefinitionEvaluationDirective(
        ModelRequest request,
        out DefinitionEvaluationCheckType checkType,
        out string? toolName)
    {
        checkType = default;
        toolName = null;
        var directive = request.Messages
            .FirstOrDefault(message =>
                message.Role == ModelRole.System
                && message.Text.StartsWith(DefinitionEvaluationHarness.SystemPrefix, StringComparison.Ordinal))
            ?.Text;
        if (directive is null)
        {
            return false;
        }

        foreach (var token in directive.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("check=", StringComparison.Ordinal)
                && Enum.TryParse(token["check=".Length..], true, out DefinitionEvaluationCheckType parsed))
            {
                checkType = parsed;
            }
            else if (token.StartsWith("tool=", StringComparison.Ordinal))
            {
                toolName = token["tool=".Length..];
            }
        }

        return Enum.IsDefined(checkType);
    }

    public const string MessagingSkillJourneyMarker = "[test:p85-journey]";

    public const string MessagingSkillJourneyMessage = "Still checking the billing case.";

    public const string MessagingSkillJourneyAnswer = "Billing review is complete.";

    public const string MessagingSkillJourneyTargetSkill = "billing.review";

    public const string MessagingSkillJourneyProcedure = "BILLING_PROCEDURE";

    private static bool TryScriptMessagingSkillJourney(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        if (!lastUser.Contains(MessagingSkillJourneyMarker, StringComparison.Ordinal)
            || !Offers(request, ToolCatalog.WorkspaceList))
        {
            return false;
        }

        var toolRounds = request.Messages.Count(message => message.Role == ModelRole.Tool);
        if (toolRounds == 0)
        {
            events =
            [
                new ModelToolCallEvent(new ModelToolCall("p85-work", ToolCatalog.WorkspaceList, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ];
            return true;
        }

        if (toolRounds == 1 && Offers(request, ToolCatalog.AppMessageSend))
        {
            events =
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "p85-message",
                    ToolCatalog.AppMessageSend,
                    JsonSerializer.Serialize(new { text = MessagingSkillJourneyMessage }))),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ];
            return true;
        }

        if (toolRounds == 2)
        {
            events =
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "p85-load",
                    ToolCatalog.SkillsLoad,
                    JsonSerializer.Serialize(new { ids = new[] { MessagingSkillJourneyTargetSkill } }))),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ];
            return true;
        }

        var prompt = string.Join('\n', request.Messages.Select(message => message.Text));
        if (!prompt.Contains(MessagingSkillJourneyProcedure, StringComparison.Ordinal))
        {
            events =
            [
                new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "Synthetic journey missed the loaded procedure."))
            ];
            return true;
        }

        events =
        [
            new ModelSemanticResponseReady(
                new ModelSemanticResponse(
                    MessagingSkillJourneyAnswer,
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    [])),
            new ModelCompleted(ModelStopReason.Completed)
        ];
        return true;
    }

    public const string BrowserChallengeMarker = "human verification";

    public const string BrowserSignupMarker = "signup fixture";

    public const string BrowserSignupAnswer =
        "I've reached the registration page. Please complete it in the browser and tell me to continue.";

    public const string BrowserSignupContinueAnswer = "The account page is ready.";

    public const string BrowserChallengeAnswer =
        "The page is asking for human verification. The browser is still open. Complete the check manually, then tell me to continue.";

    public const string BrowserChallengeContinueAnswer =
        "The verification page is still open. Tell me when you have finished the check.";

    public const string BrowserDenialMarker = "https://example.invalid";

    public const string BrowserDenialAnswer = "That site is outside the trusted browser scope.";

    public const string BrowserRecordMarker = "record AC-1042";

    public const string BrowserRecordMessage = "I found the record. I'm checking the details now.";

    public const string BrowserRecordAnswer = "AC-1042 is In review.";

    public const string BrowserRecordSkillId = "browser.record.lookup";

    public const string ProductPublishMarker = "Publish SKU AC-KBD-001";

    public const string ProductPublishStopMarker = "stop after the click";

    public const string ProductPublishLoginMarker = "login wall";

    public const string ProductPublishSkillId = "store.product.manage";

    public const string ProductPublishVerifiedAnswer = "AC Keyboard is published at $99.";

    public const string ProductPublishUnverifiedAnswer =
        "I clicked publish. The storefront was not checked, so I cannot confirm the product.";

    public const string ProductPublishLoginAnswer = "The page needs a person to sign in. I stopped.";

    private static bool TryScriptBrowserSignup(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var users = request.Messages
            .Where(message => message.Role == ModelRole.User)
            .Select(message => message.Text ?? string.Empty)
            .ToArray();
        if (users.Length == 0)
        {
            return false;
        }

        var continuing = users.Length > 1
            && users[^1].Trim().Equals("continue", StringComparison.OrdinalIgnoreCase)
            && users.Take(users.Length - 1).Any(text => text.Contains(BrowserSignupMarker, StringComparison.OrdinalIgnoreCase));
        var opening = users[^1].Contains(BrowserSignupMarker, StringComparison.OrdinalIgnoreCase);
        if (!continuing && !opening)
        {
            return false;
        }

        var offersBrowser = Offers(request, ToolCatalog.BrowserNavigate);
        var startUrl = string.Empty;
        if (offersBrowser && !TryReadTrustedBrowserStart(request, out startUrl))
        {
            return false;
        }

        var rounds = ToolRoundsSinceLastUser(request.Messages);
        if (!offersBrowser)
        {
            if (rounds == 0)
            {
                return false;
            }

            events =
            [
                new ModelTextDelta(continuing ? BrowserSignupContinueAnswer : BrowserSignupAnswer),
                new ModelCompleted(ModelStopReason.Completed)
            ];
            return true;
        }

        if (continuing)
        {
            if (rounds == 0)
            {
                events = ToolTurn(
                    "signup-account",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = startUrl.TrimEnd('/') + "/account" }));
                return true;
            }

            events =
            [
                new ModelTextDelta(BrowserSignupContinueAnswer),
                new ModelCompleted(ModelStopReason.Completed)
            ];
            return true;
        }

        switch (rounds)
        {
            case 0:
                events = ToolTurn(
                    "signup-open",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = startUrl.TrimEnd('/') + "/signup" }));
                return true;
            default:
                events =
                [
                    new ModelTextDelta(BrowserSignupAnswer),
                    new ModelCompleted(ModelStopReason.Completed)
                ];
                return true;
        }
    }

    private static bool TryScriptBrowserChallenge(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var users = request.Messages
            .Where(message => message.Role == ModelRole.User)
            .Select(message => message.Text ?? string.Empty)
            .ToArray();
        if (users.Length == 0)
        {
            return false;
        }

        var continuing = users.Length > 1
            && users[^1].Trim().Equals("continue", StringComparison.OrdinalIgnoreCase)
            && users.Take(users.Length - 1).Any(text => text.Contains(BrowserChallengeMarker, StringComparison.OrdinalIgnoreCase));
        var opening = users[^1].Contains(BrowserChallengeMarker, StringComparison.OrdinalIgnoreCase);
        if (!continuing && !opening)
        {
            return false;
        }

        var offersBrowser = Offers(request, ToolCatalog.BrowserNavigate);
        var startUrl = string.Empty;
        if (offersBrowser && !TryReadTrustedBrowserStart(request, out startUrl))
        {
            return false;
        }

        var rounds = ToolRoundsSinceLastUser(request.Messages);
        if (!offersBrowser)
        {
            if (rounds == 0)
            {
                return false;
            }

            events =
            [
                new ModelTextDelta(continuing ? BrowserChallengeContinueAnswer : BrowserChallengeAnswer),
                new ModelCompleted(ModelStopReason.Completed)
            ];
            return true;
        }

        if (continuing)
        {
            if (rounds == 0)
            {
                events = ToolTurn("p9-challenge-again", ToolCatalog.BrowserObserve, "{}");
                return true;
            }

            events =
            [
                new ModelTextDelta(BrowserChallengeContinueAnswer),
                new ModelCompleted(ModelStopReason.Completed)
            ];
            return true;
        }

        switch (rounds)
        {
            case 0:
                var challengeUrl = startUrl.TrimEnd('/') + "/challenge";
                events = ToolTurn(
                    "p9-challenge-open",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = challengeUrl }));
                return true;
            case 1:
                if (!Offers(request, ToolCatalog.BrowserObserve))
                {
                    events =
                    [
                        new ModelTextDelta(BrowserChallengeAnswer),
                        new ModelCompleted(ModelStopReason.Completed)
                    ];
                    return true;
                }

                events = ToolTurn("p9-challenge-observe", ToolCatalog.BrowserObserve, "{}");
                return true;
            default:
                events =
                [
                    new ModelTextDelta(BrowserChallengeAnswer),
                    new ModelCompleted(ModelStopReason.Completed)
                ];
                return true;
        }
    }

    private static bool TryScriptBrowserTargetDenied(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        if (!lastUser.Contains(BrowserDenialMarker, StringComparison.Ordinal)
            || !Offers(request, ToolCatalog.BrowserNavigate))
        {
            return false;
        }

        if (ToolRoundsSinceLastUser(request.Messages) == 0)
        {
            events = ToolTurn(
                "p9-denied",
                ToolCatalog.BrowserNavigate,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = "https://example.invalid/escape" }));
            return true;
        }

        events =
        [
            new ModelTextDelta(BrowserDenialAnswer),
            new ModelCompleted(ModelStopReason.Completed)
        ];
        return true;
    }

    private static bool TryScriptProductPublish(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        if (!lastUser.Contains(ProductPublishMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var login = lastUser.Contains(ProductPublishLoginMarker, StringComparison.OrdinalIgnoreCase);
        var stopAfterClick = lastUser.Contains(ProductPublishStopMarker, StringComparison.OrdinalIgnoreCase);
        var offersBrowser = Offers(request, ToolCatalog.BrowserNavigate);
        var startUrl = string.Empty;
        if (offersBrowser && !TryReadTrustedBrowserStart(request, out startUrl))
        {
            return false;
        }

        var rounds = ToolRoundsSinceLastUser(request.Messages);
        if (!offersBrowser)
        {
            if (rounds == 0)
            {
                return false;
            }

            events = ProductPublishAnswer(login, stopAfterClick);
            return true;
        }

        var origin = startUrl.TrimEnd('/');
        if (login)
        {
            if (rounds == 0)
            {
                events = ToolTurn(
                    "product-login",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = origin + "/login" }));
                return true;
            }

            events = ProductPublishAnswer(login: true, stopAfterClick: false);
            return true;
        }

        switch (rounds)
        {
            case 0:
                events = ToolTurn(
                    "product-skill",
                    ToolCatalog.SkillsLoad,
                    JsonSerializer.Serialize(new { ids = new[] { ProductPublishSkillId } }));
                return true;
            case 1:
                events = ToolTurn(
                    "product-open",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = origin + "/" }));
                return true;
            case 2:
                events = ToolTurn("product-observe", ToolCatalog.BrowserObserve, "{}");
                return true;
            case 3:
                return TryAct("product-publish", "click", ElementRef(request.Messages, "Publish"), null, out events);
            case 4 when !stopAfterClick:
                events = ToolTurn(
                    "product-storefront",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = origin + "/storefront" }));
                return true;
            case 5 when !stopAfterClick:
                events = ToolTurn("product-storefront-observe", ToolCatalog.BrowserObserve, "{}");
                return true;
            default:
                events = ProductPublishAnswer(login: false, stopAfterClick);
                return true;
        }
    }

    private static IReadOnlyList<ModelGenerationEvent> ProductPublishAnswer(bool login, bool stopAfterClick) =>
    [
        new ModelTextDelta(login
            ? ProductPublishLoginAnswer
            : stopAfterClick
                ? ProductPublishUnverifiedAnswer
                : ProductPublishVerifiedAnswer),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static bool TryScriptBrowserRecordLookup(
        ModelRequest request,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        events = [];
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        if (!lastUser.Contains(BrowserRecordMarker, StringComparison.Ordinal)
            || !Offers(request, ToolCatalog.BrowserNavigate)
            || !TryReadTrustedBrowserStart(request, out var startUrl))
        {
            return false;
        }

        var rounds = ToolRoundsSinceLastUser(request.Messages);
        switch (rounds)
        {
            case 0:
                events = ToolTurn(
                    "p9-navigate",
                    ToolCatalog.BrowserNavigate,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = startUrl }));
                return true;
            case 1:
            case 6:
            case 8:
                events = ToolTurn($"p9-observe-{rounds}", ToolCatalog.BrowserObserve, "{}");
                return true;
            case 2:
                events = ToolTurn(
                    "p9-skill",
                    ToolCatalog.SkillsLoad,
                    JsonSerializer.Serialize(new { ids = new[] { BrowserRecordSkillId } }));
                return true;
            case 3 when Offers(request, ToolCatalog.AppMessageSend):
                events = ToolTurn(
                    "p9-message",
                    ToolCatalog.AppMessageSend,
                    JsonSerializer.Serialize(new { text = BrowserRecordMessage }));
                return true;
            case 4:
                return TryAct("fill", "fill", ElementRef(request.Messages, "Record"), "AC-1042", out events);
            case 5:
                return TryAct("search", "click", ElementRef(request.Messages, "Search"), null, out events);
            case 7:
                return TryAct("open", "click", ElementRef(request.Messages, "AC-1042"), null, out events);
            default:
                events =
                [
                    new ModelTextDelta(BrowserRecordAnswer),
                    new ModelCompleted(ModelStopReason.Completed)
                ];
                return true;
        }
    }

    private static bool TryReadTrustedBrowserStart(ModelRequest request, out string url)
    {
        url = string.Empty;
        var description = request.Tools?
            .FirstOrDefault(tool => string.Equals(tool.Name, ToolCatalog.BrowserNavigate, StringComparison.Ordinal))
            ?.Description;
        const string prefix = "Trusted browser start: ";
        if (string.IsNullOrEmpty(description))
        {
            return false;
        }

        var start = description.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var rest = description[(start + prefix.Length)..].Trim();
        if (rest.EndsWith('.'))
        {
            rest = rest[..^1];
        }

        url = rest;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.IsLoopback
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool TryAct(
        string callId,
        string operation,
        string? reference,
        string? value,
        out IReadOnlyList<ModelGenerationEvent> events)
    {
        if (string.IsNullOrEmpty(reference))
        {
            events =
            [
                new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "Synthetic browser journey missed an element reference."))
            ];
            return true;
        }

        var arguments = new Dictionary<string, string>
        {
            ["operation"] = operation,
            ["ref"] = reference
        };
        if (value is not null)
        {
            arguments["value"] = value;
        }

        events = ToolTurn("p9-" + callId, ToolCatalog.BrowserAct, JsonSerializer.Serialize(arguments));
        return true;
    }

    private static string? ElementRef(IReadOnlyList<ModelMessage> messages, string name)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role == ModelRole.User)
            {
                return null;
            }

            if (message.Role != ModelRole.Tool || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(message.Text);
                if (!document.RootElement.TryGetProperty("elements", out var elements)
                    || elements.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in elements.EnumerateArray())
                {
                    if (element.TryGetProperty("name", out var elementName)
                        && string.Equals(elementName.GetString(), name, StringComparison.Ordinal)
                        && element.TryGetProperty("ref", out var reference)
                        && reference.GetString() is { Length: > 0 } token)
                    {
                        return token;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static IReadOnlyList<ModelGenerationEvent> ToolTurn(string callId, string name, string arguments) =>
    [
        new ModelToolCallEvent(new ModelToolCall(callId, name, arguments)),
        new ModelCompleted(ModelStopReason.ToolCalls)
    ];

    public const string EmailHarnessMarker = "email harness";

    public const string SensitiveApprovalMarker = "sensitive approval";

    public const string ScheduleForceMarker = "[test:schedule-force]";

    public const string HistoricalImageRereadMarker = "[test:historical-image-reread]";

    public const string HistoricalImageRereadAnswer =
        "Synthetic historical image reread: observed sanitized image content.";

    private static bool TryReadManifestAttachmentId(ModelRequest request, out Guid attachmentId)
    {
        attachmentId = Guid.Empty;
        var manifest = request.Messages.FirstOrDefault(message =>
            message.Role == ModelRole.System
            && message.Text.Contains("Files available in this session", StringComparison.Ordinal));
        if (manifest is null)
        {
            return false;
        }

        const string key = "\"attachmentId\":\"";
        var start = manifest.Text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += key.Length;
        var end = manifest.Text.IndexOf('"', start);
        return end > start && Guid.TryParse(manifest.Text[start..end], out attachmentId);
    }

    private static bool Offers(ModelRequest request, string name) =>
        request.Tools?.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)) == true;

    private static int ToolRoundsSinceLastUser(IReadOnlyList<ModelMessage> messages)
    {
        var count = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            switch (messages[index].Role)
            {
                case ModelRole.Tool:
                    count++;
                    break;
                case ModelRole.User:
                    return count;
            }
        }

        return count;
    }

    private static string LastToolTextSinceLastUser(IReadOnlyList<ModelMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ModelRole.User)
            {
                return string.Empty;
            }

            if (messages[index].Role == ModelRole.Tool)
            {
                return messages[index].Text ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool TryScriptEmailHarness(
        ModelRequest request,
        int toolRounds,
        string lastUser,
        string lastTool,
        out ModelGenerationEvent toolEvent)
    {
        toolEvent = null!;
        if (!lastUser.Contains(EmailHarnessMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        switch (toolRounds)
        {
            case 0 when Offers(request, ToolCatalog.EmailSearch):
                toolEvent = new ModelToolCallEvent(new ModelToolCall(
                    "call-email-search",
                    ToolCatalog.EmailSearch,
                    """{"query":"harness","limit":5}"""));
                return true;
            case 1 when Offers(request, ToolCatalog.EmailRead):
                var messageId = ExtractFirstSearchMessageId(lastTool) ?? "syn-msg-harness";
                toolEvent = new ModelToolCallEvent(new ModelToolCall(
                    "call-email-read",
                    ToolCatalog.EmailRead,
                    $$"""{"messageId":"{{EscapeJson(messageId)}}"}"""));
                return true;
            case 2 when Offers(request, ToolCatalog.EmailCreateDraft):
                toolEvent = new ModelToolCallEvent(new ModelToolCall(
                    "call-email-draft",
                    ToolCatalog.EmailCreateDraft,
                    """{"to":["recipient@example.test"],"cc":[],"bcc":["bcc@example.test"],"subject":"Harness draft","body":"Synthetic email harness send path."}"""));
                return true;
            case 3 when Offers(request, ToolCatalog.EmailSend):
                var draftId = ExtractJsonString(lastTool, "draftId") ?? "syn-draft-missing";
                toolEvent = new ModelToolCallEvent(new ModelToolCall(
                    "call-email-send",
                    ToolCatalog.EmailSend,
                    $$"""{"draftId":"{{EscapeJson(draftId)}}"}"""));
                return true;
            default:
                return false;
        }
    }

    private static string? ExtractFirstSearchMessageId(string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(toolJson);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("messageId", out var messageId) && messageId.ValueKind == JsonValueKind.String)
                {
                    return messageId.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? ExtractJsonString(string toolJson, string property)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(toolJson);
            if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string ExtractArtifactId(string toolJson)
    {
        const string key = "\"artifactId\":\"";
        var start = toolJson.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return FixtureArtifactReferenceAuthorizer.AuthorizedId;
        }

        start += key.Length;
        var end = toolJson.IndexOf('"', start);
        return end < 0 ? FixtureArtifactReferenceAuthorizer.AuthorizedId : toolJson[start..end];
    }

    internal const string SteerProbeMarker = "[test:steer-probe]";
    internal const string DurableStreamProbeMarker = "[test:durable-stream]";
    internal const string DurableStreamLongHoldMarker = "[test:durable-stream-long]";

    private static bool RequestsNativeJson(ModelRequest request) =>
        request.ResponseContract is not null
        && request.Messages.All(message =>
            message.Role != ModelRole.System
            || !message.Text.Contains(AssistantResponseSchema.CompatibilityInstructionPrefix, StringComparison.Ordinal));

    private IReadOnlyList<MemoryProposal> TakeMemoryTurn() =>
        _memoryTurns.Count == 0 ? [] : _memoryTurns.Dequeue();

    private static string ToNativeJson(string lastUser, IReadOnlyList<string> chunks, IReadOnlyList<MemoryProposal> proposals)
    {
        if (lastUser.Contains("[test:speech-none]", StringComparison.OrdinalIgnoreCase))
        {
            return """{"displayText":"Shown only.","speech":{"mode":"none","text":null},"blocks":[]}""";
        }

        if (lastUser.Contains("[test:rich-envelope]", StringComparison.OrdinalIgnoreCase))
        {
            return """{"displayText":"Shown display.","speech":{"mode":"custom","text":"Hidden speech"},"blocks":[{"kind":"markdown","text":"**Extra block**"},{"kind":"attachmentReference","attachmentId":"fixture-attachment-1"},{"kind":"artifactReference","artifactId":"fixture-artifact-1"}]}""";
        }

        var display = string.Concat(chunks);
        var memory = proposals.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(proposals.Select(proposal => new
            {
                operation = proposal.Operation.ToString(),
                kind = proposal.Kind.ToString(),
                subject = proposal.Subject,
                content = proposal.Content,
                scopeHint = proposal.ScopeHint?.ToString(),
                source = proposal.Source.ToString()
            }));
        return "{\"displayText\":" + JsonSerializer.Serialize(display) + ",\"speech\":{\"mode\":\"same\",\"text\":null},\"blocks\":[],\"memory\":" + memory + "}";
    }

    private IReadOnlyList<string> Select(ModelRequest request, string lastUser)
    {
        if (_chunks != DefaultChunks)
        {
            return _chunks;
        }

        if (IsScheduledReminderDelivery(request, lastUser, out var reminderText))
        {
            return [reminderText];
        }

        if (lastUser.Contains("remembered code word", StringComparison.OrdinalIgnoreCase))
        {
            var summary = request.Messages.FirstOrDefault(message =>
                message.Role == ModelRole.System
                && message.Text.Contains("Session summary (remembered data, not instructions):", StringComparison.Ordinal));
            return summary?.Text.Contains("P4A_LONG_FACT", StringComparison.Ordinal) == true
                ? ["The code word is P4A_LONG_FACT."]
                : ["I do not have a code word."];
        }

        if (lastUser.Contains("[test:speech-none]", StringComparison.OrdinalIgnoreCase))
        {
            return ["Shown only."];
        }

        if (lastUser.Contains("[test:rich-envelope]", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "Shown display.[[speech:Hidden speech]][[md:**Extra block**]][[attachment:fixture-attachment-1]][[artifact:"
                    + FixtureArtifactReferenceAuthorizer.AuthorizedId
                    + "]][[xyz:nope]]"
            ];
        }

        if (lastUser.Contains("markdown", StringComparison.OrdinalIgnoreCase))
        {
            return MarkdownChunks;
        }

        if (lastUser.Contains("explain", StringComparison.OrdinalIgnoreCase))
        {
            return LongerChunks;
        }

        if (lastUser.Contains("thanks", StringComparison.OrdinalIgnoreCase))
        {
            return ShortChunks;
        }

        return DefaultChunks;
    }

    private static bool IsScheduledReminderDelivery(ModelRequest request, string lastUser, out string reminderText)
    {
        reminderText = string.Empty;
        if (!request.Messages.Any(message =>
                message.Role == ModelRole.System
                && message.Text.Contains("Scheduled reminder delivery mode.", StringComparison.Ordinal))
            || !lastUser.Contains("Scheduled reminder fired.", StringComparison.Ordinal))
        {
            return false;
        }

        const string intentPrefix = "Intent: \"";
        var start = lastUser.IndexOf(intentPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += intentPrefix.Length;
        var end = lastUser.IndexOf('"', start);
        if (end <= start)
        {
            return false;
        }

        var intent = lastUser[start..end].Trim();
        if (intent.Length == 0)
        {
            return false;
        }

        reminderText = intent.Contains("check the oven", StringComparison.OrdinalIgnoreCase)
            ? "Oven is ready."
            : $"Reminder: {intent}.";
        return true;
    }

    private static bool IsCompactionRequest(ModelRequest request) =>
        request.Messages.Any(message => message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal));

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateCompactionAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _compactionStarted?.TrySetResult();
        switch (_compactionFixture)
        {
            case CompactionFixture.Throw:
                throw new InvalidOperationException("Synthetic compaction failure.");
            case CompactionFixture.Empty:
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            case CompactionFixture.Malformed:
                yield return new ModelTextDelta(ConversationCompactor.Marker + " not a summary");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            case CompactionFixture.Oversized:
                yield return new ModelTextDelta(new string('s', CompactionPolicy.MaxSummaryCharacters + 1));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            case CompactionFixture.Late:
                if (_compactionRelease is not null)
                {
                    await _compactionRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                yield return new ModelTextDelta(EchoCompaction(request));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            default:
                yield return new ModelTextDelta(EchoCompaction(request));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
        }
    }

    private static string EchoCompaction(ModelRequest request)
    {
        var user = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? "";
        var parts = new List<string>();
        foreach (var raw in user.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0
                || line.StartsWith("Previous ", StringComparison.Ordinal)
                || line.StartsWith("New durable", StringComparison.Ordinal)
                || line.Contains("[omitted]", StringComparison.Ordinal)
                || line.Contains("[no delivered text]", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('"') && line.EndsWith('"') && line.Length >= 2)
            {
                var quoted = line[1..^1];
                if (quoted.Length > 0 && quoted != "(none)")
                {
                    parts.Add(quoted);
                }

                continue;
            }

            var split = line.IndexOf(": ", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            var text = line[(split + 2)..].Trim();
            var attachments = text.IndexOf(" attachments:", StringComparison.Ordinal);
            if (attachments >= 0)
            {
                text = text[..attachments].TrimEnd();
            }

            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        var body = string.Join("; ", parts);
        var limit = CompactionPolicy.MaxSummaryCharacters - 20;
        if (body.Length > limit)
        {
            body = body[..limit];
        }

        return body.Length == 0 ? "Summary: no new facts" : "Summary: " + body;
    }

    private static bool TryInitiativeDecision(ModelRequest request, out string json)
    {
        json = string.Empty;
        var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
        if (system is null || !system.Contains(InitiativeEvaluator.Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            json = SyntheticInitiativeScript.Decide(root);
            return true;
        }
        catch (JsonException)
        {
            json = """{"decision":"staySilent","reason":"Synthetic initiative fallback."}""";
            return true;
        }
    }

    private bool TryCompletionDecision(ModelRequest request, out string json, out bool failed)
    {
        json = string.Empty;
        failed = false;
        var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
        if (system is null || !system.Contains(CompletionEvaluator.Marker, StringComparison.Ordinal))
        {
            return false;
        }

        if (_completionProviderFailed)
        {
            failed = true;
            return true;
        }

        json = _completionDecision
            ?? """{"decision":"continue","reason":"Synthetic completion continue."}""";
        return true;
    }
}

internal static class SyntheticInitiativeScript
{
    private const int ExaminerLongSilenceMs = 90_000;
    private const int SupportAdvanceSilenceMs = 45_000;

    public static string Decide(JsonElement root)
    {
        var speaks = ReadInt(root, "speaksThisSilencePeriod");
        var maxSpeaks = ReadInt(root, "maxPerSilencePeriod", 1);
        var consecutive = ReadInt(root, "consecutiveProactiveSpeaks");
        var consecutiveCap = ReadInt(root, "consecutiveCap", 1);
        var silenceMs = ReadInt(root, "silenceMs");
        var agentId = ReadAgentId(root);
        var trigger = ReadString(root, "trigger");
        var pendingTopic = ReadString(root, "pendingTopic");

        if (consecutive >= consecutiveCap || speaks >= maxSpeaks)
        {
            return StaySilent("Synthetic initiative cap reached.", 120_000);
        }

        if (string.Equals(trigger, "UnfinishedInteraction", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(pendingTopic))
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic unfinished interaction warrants a proactive follow-up.");
        }

        if (string.Equals(trigger, "EnvironmentUpdate", StringComparison.Ordinal))
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic environment update is actionable.");
        }

        if (string.Equals(agentId, "examiner", StringComparison.Ordinal))
        {
            return DecideExaminer(root, speaks, silenceMs);
        }

        if (string.Equals(agentId, "customer-support", StringComparison.Ordinal))
        {
            return DecideSupport(root, speaks, silenceMs);
        }

        if (speaks >= 1)
        {
            return StaySilent("Synthetic initiative avoids repeated readiness nudges.", 120_000);
        }

        return silenceMs >= 60_000
            ? Speak(InitiativeIntent.Other, "Synthetic initiative allows one proactive turn.")
            : StaySilent("Synthetic initiative waiting for longer silence.", 30_000);
    }

    private static string DecideExaminer(JsonElement root, int speaks, int silenceMs)
    {
        _ = root;
        if (speaks >= 1)
        {
            return StaySilent("Synthetic examiner avoids a second empty nudge.", 120_000);
        }

        if (silenceMs >= ExaminerLongSilenceMs)
        {
            return Speak(
                InitiativeIntent.Hint,
                "Synthetic long silence during practice exam; offer a concise scaffold or hint.");
        }

        return StaySilent("Synthetic examiner waiting for longer candidate silence.", 30_000);
    }

    private static string DecideSupport(JsonElement root, int speaks, int silenceMs)
    {
        if (!HasUnresolvedSupportContext(root))
        {
            return StaySilent("Synthetic support has no unresolved work.", 120_000);
        }

        if (speaks >= 1 && silenceMs < SupportAdvanceSilenceMs)
        {
            return StaySilent("Synthetic support pauses briefly between proactive updates.", 20_000);
        }

        if (silenceMs >= SupportAdvanceSilenceMs)
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic support advances the simulated order conversation.");
        }

        return StaySilent("Synthetic support waiting for longer silence.", 20_000);
    }

    private static bool HasUnresolvedSupportContext(JsonElement root)
    {
        if (!root.TryGetProperty("recentTurns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var sawSupportIssue = false;
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("role", out var roleNode)
                || !turn.TryGetProperty("text", out var textNode))
            {
                continue;
            }

            var role = roleNode.GetString() ?? string.Empty;
            var text = textNode.GetString() ?? string.Empty;
            if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsSupportIssue(text))
                {
                    sawSupportIssue = true;
                }

                if (sawSupportIssue && IsSupportClosure(text))
                {
                    return false;
                }
            }
        }

        return sawSupportIssue;
    }

    private static bool ContainsSupportIssue(string text) =>
        text.Contains("order", StringComparison.OrdinalIgnoreCase)
        || text.Contains("shipment", StringComparison.OrdinalIgnoreCase)
        || text.Contains("delivery", StringComparison.OrdinalIgnoreCase)
        || text.Contains("refund", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportClosure(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.Contains("that's all", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("thats all", StringComparison.OrdinalIgnoreCase)
            || (normalized.Contains("thank", StringComparison.OrdinalIgnoreCase)
                && !ContainsSupportIssue(normalized));
    }

    private static string ReadAgentId(JsonElement root)
    {
        if (!root.TryGetProperty("agent", out var agent) || !agent.TryGetProperty("id", out var idNode))
        {
            return string.Empty;
        }

        return idNode.GetString() ?? string.Empty;
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue = 0)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Number)
        {
            return defaultValue;
        }

        return node.TryGetInt32(out var value) ? value : defaultValue;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return node.GetString() ?? string.Empty;
    }

    private static string Speak(InitiativeIntent intent, string objective) =>
        JsonSerializer.Serialize(new { decision = "speak", intent = InitiativeIntents.ToWire(intent), objective });

    private static string StaySilent(string reason, int nextWaitMs) =>
        JsonSerializer.Serialize(new { decision = "staySilent", reason, nextWaitMs });
}
