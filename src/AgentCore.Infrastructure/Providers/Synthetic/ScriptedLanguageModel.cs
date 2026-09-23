using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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
    private string? _scheduleRegistrationId;
    private long _scheduleRevision;

    public ScriptedLanguageModel(
        IReadOnlyList<string>? chunks = null,
        TaskCompletionSource? releaseAfterFirstChunk = null,
        bool emitAfterCancel = false,
        bool alwaysToolCall = false,
        string? completionDecision = null,
        bool completionProviderFailed = false,
        CompactionFixture compactionFixture = CompactionFixture.Echo,
        TaskCompletionSource? compactionRelease = null,
        TaskCompletionSource? compactionStarted = null)
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

        if (request.Tools is { Count: > 0 } && (_alwaysToolCall || ShouldScriptTools(request)))
        {
            await foreach (var item in GenerateToolScriptAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var chunks = Select(request, lastUser);
        if (RequestsNativeJson(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            yield return new ModelTextDelta(ToNativeJson(lastUser, chunks));
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
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
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var lastTool = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool)?.Text ?? string.Empty;
        var lastToolMessage = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool);
        if (lastToolMessage?.Parts?.OfType<ModelImageContent>().Any() == true)
        {
            yield return new ModelTextDelta(HistoricalImageRereadAnswer);
            yield return new ModelCompleted(ModelStopReason.Completed);
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

        if (TryScriptSchedule(request, toolRounds, lastUser, lastTool, out var scheduleEvent))
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
        if (!Offers(request, ToolCatalog.TriggerScheduleOnce) && !Offers(request, ToolCatalog.TriggerList))
        {
            return false;
        }

        var scheduleTurn = IsScheduleTurn(lastUser);
        if (!scheduleTurn)
        {
            return false;
        }

        if (toolRounds > 0)
        {
            RememberSchedule(lastTool);
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

        if (lastUser.Contains("remind me", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerScheduleOnce, """{"intent":"Call John","relativeDayOffset":1,"localTime":"09:00"}""");
            return true;
        }

        if (lastUser.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerScheduleOnce, """{"intent":"Different","relativeDayOffset":2,"localTime":"15:00"}""");
            return true;
        }

        if (lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("what reminders", StringComparison.OrdinalIgnoreCase))
        {
            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerList, "{}");
            return true;
        }

        if (lastUser.Contains("move that reminder", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("reschedule", StringComparison.OrdinalIgnoreCase))
        {
            if (_scheduleRegistrationId is null)
            {
                return false;
            }

            toolEvent = ScheduleCall(
                toolRounds,
                ToolCatalog.TriggerUpdate,
                ScheduleRememberedArgs(includeTime: true, ClockFromMove(lastUser)));
            return true;
        }

        if (lastUser.Contains("cancel that reminder", StringComparison.OrdinalIgnoreCase))
        {
            if (_scheduleRegistrationId is null)
            {
                return false;
            }

            toolEvent = ScheduleCall(toolRounds, ToolCatalog.TriggerCancel, ScheduleRememberedArgs(includeTime: false, "10:00"));
            return true;
        }

        return false;
    }

    private static bool IsScheduleTurn(string lastUser) =>
        lastUser.Contains("remind me", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("every monday", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("list schedules", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("what reminders", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("move that schedule", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("move that reminder", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("cancel that schedule", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("cancel that reminder", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains("reschedule", StringComparison.OrdinalIgnoreCase)
        || lastUser.Contains(ScheduleForceMarker, StringComparison.OrdinalIgnoreCase)
        || lastUser.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

    private void RememberSchedule(string lastTool)
    {
        if (string.IsNullOrWhiteSpace(lastTool))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(lastTool);
            var root = document.RootElement;
            if (root.TryGetProperty("registrations", out var rows) && rows.GetArrayLength() > 0)
            {
                root = rows[0];
            }

            if (!root.TryGetProperty("registrationId", out var id) || !root.TryGetProperty("revision", out var revision))
            {
                return;
            }

            _scheduleRegistrationId = id.GetString();
            _scheduleRevision = revision.GetInt64();
        }
        catch (JsonException)
        {
        }
    }

    private string ScheduleRememberedArgs(bool includeTime, string localTime) =>
        includeTime
            ? $$"""{"registrationId":"{{_scheduleRegistrationId}}","expectedRevision":{{_scheduleRevision}},"intent":"Call John","relativeDayOffset":1,"localTime":"{{localTime}}"}"""
            : $$"""{"registrationId":"{{_scheduleRegistrationId}}","expectedRevision":{{_scheduleRevision}}}""";

    private static string ClockFromMove(string text) =>
        text.Contains("to 11", StringComparison.OrdinalIgnoreCase) ? "11:00" : "10:00";

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

    private static bool ShouldScriptTools(ModelRequest request)
    {
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        return lastUser.Contains("support case", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("order 91", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("compliance case", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(HistoricalImageRereadMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(SensitiveApprovalMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(EmailHarnessMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("remind me", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("every monday", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("list my schedules", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("list schedules", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("what reminders", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("move that schedule", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("move that reminder", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("cancel that schedule", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("cancel that reminder", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("reschedule", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains(ScheduleForceMarker, StringComparison.OrdinalIgnoreCase)
            || lastUser.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
            || request.Messages.Any(message => message.Role == ModelRole.Tool);
    }

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

    private static bool RequestsNativeJson(ModelRequest request) =>
        request.ResponseContract is not null
        && request.Messages.All(message =>
            message.Role != ModelRole.System
            || !message.Text.Contains(AssistantResponseSchema.CompatibilityInstructionPrefix, StringComparison.Ordinal));

    private static string ToNativeJson(string lastUser, IReadOnlyList<string> chunks)
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
        return "{\"displayText\":" + JsonSerializer.Serialize(display) + ",\"speech\":{\"mode\":\"same\",\"text\":null},\"blocks\":[]}";
    }

    private IReadOnlyList<string> Select(ModelRequest request, string lastUser)
    {
        if (_chunks != DefaultChunks)
        {
            return _chunks;
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
