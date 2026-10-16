using AgentCore.Application.Context;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentCore.Application.Triggers;

public enum TriggerCommandAuthorizationDecision
{
    Deny,
    Allow,
    Ambiguous,
    ClassifierUnavailable,
    ClassifierInvalidOutput
}

public enum TriggerAuthorizationFailureReason
{
    MalformedJson, InvalidDecision, TruncatedOutput, IncompleteOutput, InvalidResponseChannel, OversizedOutput, ProviderInvalidResponse, ProviderFailure
}

public sealed record TriggerCommandAuthorizationVerdict(TriggerCommandAuthorizationDecision Decision, TriggerAuthorizationFailureReason? FailureReason = null);

public interface ITriggerCommandAuthorizer
{
    ValueTask<TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(
        string? currentUserText,
        string? conversationLanguage,
        TriggerCommandAction requestedAction,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default);

    async ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeWithDetailsAsync(string? currentUserText, string? conversationLanguage,
        TriggerCommandAction requestedAction, ScheduleConversationContext? scheduleContext = null, ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default) => new(await AuthorizeCurrentTurnAsync(currentUserText, conversationLanguage,
            requestedAction, scheduleContext, scheduleDraft, cancellationToken).ConfigureAwait(false));

    bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage);
}

public static class TriggerScheduleTurnPreflight
{
    public static bool IsScheduleRelatedTurn(
        string? currentUserText,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext = null)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return false;
        }

        var text = HeuristicTriggerCommandAuthorizer.NormalizeTurn(currentUserText);
        return text.Contains("automation", StringComparison.Ordinal) || text.Contains("core event", StringComparison.Ordinal) || text.Contains("preset", StringComparison.Ordinal) || ScheduleContinuationLanguage.LooksScheduleRelated(text, conversationLanguage, scheduleContext);
    }
}

public sealed class HeuristicTriggerCommandAuthorizer : ITriggerCommandAuthorizer
{
    private static readonly Regex TestHarnessMarker = new(
        @"\[test:[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InformationalFuture = new(
        @"\b(i'?ll be back|i have a meeting|what happens|why did you)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CapabilityQuestion = new(
        @"\b(can you|could you|do you)\b.{0,20}\b(schedule|remind)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EventAutomationCreate = new(@"^(?:please\s+|i want you to\s+)?(create|set up|configure|enable)\b.{0,80}\b(automation|core event|review recent work|consolidate continuity|improve agent harness)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AgentDirectedCreate = new(
        @"^(?:please\s+|i want you to\s+)?(remind me|notify me|ping me|alert me|nudge me|wake me|set a reminder|schedule a\b|schedule\b.{0,80}\b(at|for|in)\b|every\s+\w+\s+remind|\bsay\b.{0,60}\bto me\b|\b(in|after)\s+\d+\s*(second|seconds|sec|secs|minute|minutes|min|mins|hour|hours|hr|hrs)\b.{0,40}\b(to me|me)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AgentDirectedCreateVi = new(
        @"(nhắc tôi|báo tôi|hẹn tôi|đặt lịch|lên lịch).{0,40}(phút|giờ|ngày|mai|thứ)|\d+\s*phút\s*nữa.{0,30}(chào|nhắc|báo)\s*tôi",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListIntent = new(
        @"\b(show|list|display)\b.{0,30}\b(my\s+)?(automation|automations|reminder|reminders|schedule|schedules)\b|\bwhat\b.{0,30}\b(reminder|reminders|scheduled)\b|\bwhat do i have scheduled\b|\bwhat'?s on my schedule\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListIntentVi = new(
        @"(xem|hiển thị|liệt kê).{0,20}(nhắc|lịch|hẹn)|(nhắc|lịch).{0,20}(của tôi|nào)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UpdateIntent = new(
        @"\b(update|edit|move|reschedule|change|shift|push|delay|postpone|bump)\b.{0,30}\b(that|the|this|it|reminder|schedule|automation)\b|\b(move|reschedule)\b.{0,20}\bto\b|\bmove it to\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CancelIntent = new(
        @"\b(cancel|delete|remove|drop|clear|stop)\b.{0,30}\b(that|the|this|it|reminder|schedule|automation)\b|\bdelete that reminder\b|\bcancel that reminder\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A definite non-command must not become authorized by a classifier or a substring match.
    internal static bool IsClearlyNonAuthorizing(string text, TriggerCommandAction action = TriggerCommandAction.Create) =>
        IsExplicitlyNegated(text) || (action != TriggerCommandAction.List && NonCommandPrefix.IsMatch(text)) || InformationalFuture.IsMatch(text)
        || action == TriggerCommandAction.Create && AutomationActivation.IsActivationDirective(text);

    private static readonly Regex NonCommandPrefix = new(
        @"^(?:please\s+)?(?:can you|could you|do you|would you|what|why|how|did|does|is it|yesterday|last\s+\w+|previously|earlier|i (?:asked|said|used|wanted)|you (?:said|used|were))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsExplicitlyNegated(string text) => Regex.IsMatch(text,
        @"\b(don['’]?t|do not|never)\b.{0,60}\b(enable|activate|resume|unpause|turn on|disable|deactivate|pause|turn off|create|set|configure|update|edit|change|cancel|delete|remove|stop|greet|say|tell|send|message|check|wake|notify|ping|alert|nudge|remind|schedule|think)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ValueTask<TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(
        string? currentUserText,
        string? conversationLanguage,
        TriggerCommandAction requestedAction,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return ValueTask.FromResult(TriggerCommandAuthorizationDecision.Deny);
        }

        var text = NormalizeTurn(currentUserText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.FromResult(TriggerCommandAuthorizationDecision.Deny);
        }

        if (IsClearlyNonAuthorizing(text, requestedAction))
            return ValueTask.FromResult(TriggerCommandAuthorizationDecision.Deny);

        var allowed = requestedAction switch
        {
            TriggerCommandAction.Create => MatchesCreate(text, conversationLanguage, scheduleContext, scheduleDraft),
            TriggerCommandAction.List => MatchesList(text, conversationLanguage, scheduleContext),
            TriggerCommandAction.Update => MatchesUpdate(text, conversationLanguage, scheduleContext),
            TriggerCommandAction.Cancel => MatchesCancel(text, conversationLanguage, scheduleContext),
            TriggerCommandAction.Enable => AutomationActivation.Matches(text, true, scheduleContext),
            TriggerCommandAction.Disable => AutomationActivation.Matches(text, false, scheduleContext),
            _ => false
        };

        return ValueTask.FromResult(
            allowed
                ? TriggerCommandAuthorizationDecision.Allow
                : TriggerCommandAuthorizationDecision.Deny);
    }

    public bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return false;
        }

        var trimmed = NormalizeTurn(currentUserText);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var normalized = trimmed.ToLowerInvariant();
        if (normalized is "yes" or "y" or "yep" or "yeah" or "ok" or "okay" or "sure" or "confirm" or "confirmed"
            or "please do" or "go ahead" or "do that" or "do it" or "sounds good" or "that works")
        {
            return true;
        }

        if (normalized is "yes, please" or "yes please" or "i approve" or "approve it" or "approved" or "approve")
        {
            return true;
        }

        if (normalized.StartsWith("yes ", StringComparison.Ordinal) && normalized.Length <= 32)
        {
            return true;
        }

        if (PrefersVietnamese(conversationLanguage))
        {
            return VietnameseConfirmation.IsMatch(trimmed);
        }

        return false;
    }

    internal static string NormalizeTurn(string currentUserText) =>
        TestHarnessMarker.Replace(currentUserText.Trim(), string.Empty).Trim();

    internal static bool MatchesCreate(
        string text,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null)
    {
        if (IsClearlyNonAuthorizing(text) || TimedScheduleIntent.HasQualification(text)) return false;

        if (scheduleDraft is { IsActive: true }
            && ScheduleIntervalLanguage.IsIntervalOnlyCorrection(text, conversationLanguage))
        {
            return true;
        }

        if (CapabilityQuestion.IsMatch(text))
        {
            return false;
        }

        if (ScheduleContinuationLanguage.MatchesContinuationCreate(text, scheduleContext))
        {
            return true;
        }

        // Recognized timed requests have one gate: unsupported/qualified forms must
        // reach classification instead of falling through a broader directive pattern.
        if (TimedScheduleIntent.HasTiming(text))
        {
            return TimedScheduleIntent.Matches(text);
        }

        if (PrefersVietnamese(conversationLanguage) && AgentDirectedCreateVi.IsMatch(text))
        {
            return true;
        }

        return AgentDirectedCreate.IsMatch(text) || EventAutomationCreate.IsMatch(text)
            || Regex.IsMatch(text, @"^(?:please\s+)?when\b.{0,40}\border\b.{0,40}\b(placed|created|arrives)\b.{0,80}\b(review|check|notify|run)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool MatchesList(string text, string? conversationLanguage, ScheduleConversationContext? context = null) =>
        ListIntent.IsMatch(text) || (PrefersVietnamese(conversationLanguage) && ListIntentVi.IsMatch(text))
        || AutomationActivation.Matches(text, true, context) || AutomationActivation.Matches(text, false, context)
        || (context is { IsReferentAvailable: true } || Regex.IsMatch(text, @"\b(automation|schedule|reminder)s?\b", RegexOptions.IgnoreCase))
            && (MatchesUpdate(text, conversationLanguage, context) || MatchesCancel(text, conversationLanguage, context));

    internal static bool MatchesUpdate(
        string text,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext = null) =>
        ScheduleContinuationLanguage.MatchesContinuationUpdate(text, scheduleContext)
        || UpdateIntent.IsMatch(text);

    internal static bool MatchesCancel(
        string text,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext = null) =>
        ScheduleContinuationLanguage.MatchesContinuationCancel(text, scheduleContext)
        || CancelIntent.IsMatch(text);

    private static bool PrefersVietnamese(string? conversationLanguage) =>
        ConversationLanguagePolicy.IsAuto(conversationLanguage ?? ConversationLanguagePolicy.Auto)
        || string.Equals(conversationLanguage, "vi", StringComparison.OrdinalIgnoreCase)
        || string.Equals(conversationLanguage, "vi-VN", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex VietnameseConfirmation = new(
        @"^(vâng|ừ|uh|uhm|đồng ý|ok|oke|được|làm đi|xác nhận)(\s+.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

public sealed class ModelTriggerCommandAuthorizer(
    ILanguageModel languageModel,
    HeuristicTriggerCommandAuthorizer heuristic,
    string? reasoningEffort = null,
    ILogger<ModelTriggerCommandAuthorizer>? logger = null,
    ModelDescriptor? descriptor = null,
    TimeProvider? timeProvider = null) : ITriggerCommandAuthorizer
{
    private const int InitialOutputTokens = 512;
    private const int RecoveryOutputTokens = 1024;
    private static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async ValueTask<TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(string? currentUserText, string? conversationLanguage,
        TriggerCommandAction requestedAction, ScheduleConversationContext? scheduleContext = null, ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default) => (await AuthorizeWithDetailsAsync(currentUserText, conversationLanguage,
            requestedAction, scheduleContext, scheduleDraft, cancellationToken).ConfigureAwait(false)).Decision;

    public async ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeWithDetailsAsync(
        string? currentUserText,
        string? conversationLanguage,
        TriggerCommandAction requestedAction,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return new(TriggerCommandAuthorizationDecision.Deny);
        }

        var text = HeuristicTriggerCommandAuthorizer.NormalizeTurn(currentUserText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(TriggerCommandAuthorizationDecision.Deny);
        }

        if (HeuristicTriggerCommandAuthorizer.IsClearlyNonAuthorizing(text, requestedAction))
            return new(TriggerCommandAuthorizationDecision.Deny);

        var heuristicDecision = await heuristic.AuthorizeCurrentTurnAsync(
            text,
            conversationLanguage,
            requestedAction,
            scheduleContext,
            scheduleDraft,
            cancellationToken).ConfigureAwait(false);
        if (heuristicDecision == TriggerCommandAuthorizationDecision.Allow)
        {
            return new(heuristicDecision);
        }

        var request = ContextAssembler.Create(Guid.Empty, new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract), ContextSection.Core(CoreProcedure.TriggerAuthorization),
            ContextSection.Runtime(ContextSectionId.RunTask, ContextSourceKind.AgentRun,
                $"Proposed action: {requestedAction}; conversation language: {conversationLanguage ?? "auto"}.", semantics: ContextSemantics.TrustedFact),
            ContextSection.Evidence(ContextSectionId.History, ContextSourceKind.Session, string.Join('\n',
                (scheduleContext?.ToPromptLines() ?? []).Concat(scheduleDraft?.ToPromptLines() ?? [])))
        ], purpose: ContextPurpose.Platform), [new(ModelRole.User, text)], maxOutputTokens: InitialOutputTokens, temperature: 0, reasoningEffort: reasoningEffort);

        // Both attempts share one deadline, further bounded by the calling tool/turn.
        using var timeout = new CancellationTokenSource(ClassificationTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = time.GetTimestamp();
            using var activity = RuntimeTelemetry.Activity.StartActivity("automation.authorization.classifier");
            ClassifierRead read;
            TriggerCommandAuthorizationVerdict verdict;
            var valid = false;
            try
            {
                read = await ReadTextAsync(languageModel, request, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (read.Failure is { } failure) verdict = failure;
                else
                {
                    valid = TryParseDecision(read.Text, out var decision, out var reason);
                    verdict = valid ? new(decision) : new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, reason);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                read = new(string.Empty, null, null);
                verdict = new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure);
            }
            catch (OperationCanceledException)
            {
                RecordAttempt("Cancelled", null, false);
                throw;
            }
            catch (Exception)
            {
                RecordAttempt("UnhandledFailure", null, false);
                throw;
            }
            var terminal = deadline.IsCancellationRequested ? "DeadlineExceeded"
                : read.ProviderCode?.ToString() ?? read.Terminal?.Reason.ToString() ?? verdict.FailureReason?.ToString() ?? verdict.Decision.ToString();
            RecordAttempt(terminal, read.Terminal?.OutputTokens, valid);
            if (attempt == 2 || verdict.FailureReason != TriggerAuthorizationFailureReason.TruncatedOutput || deadline.IsCancellationRequested)
                return verdict;
            // Never carry over partial text, tools, or a proposed decision.
            request = request with { MaxOutputTokens = RecoveryOutputTokens };

            void RecordAttempt(string stop, int? outputTokens, bool validDecision)
            {
                var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
                activity?.SetTag("action", requestedAction.ToString());
                activity?.SetTag("catalog", SafeMetadata(descriptor?.Key));
                activity?.SetTag("provider", SafeMetadata(descriptor?.ProviderAlias));
                activity?.SetTag("model", SafeMetadata(descriptor?.ModelId));
                activity?.SetTag("transport", descriptor?.Transport.ToString() ?? "Unknown");
                activity?.SetTag("reasoning_effort", SafeMetadata(reasoningEffort));
                activity?.SetTag("attempt", attempt);
                activity?.SetTag("max_attempts", 2);
                activity?.SetTag("max_output_tokens", request.MaxOutputTokens);
                activity?.SetTag("elapsed_ms", elapsed);
                activity?.SetTag("terminal", stop);
                activity?.SetTag("output_tokens", outputTokens);
                activity?.SetTag("valid_decision", validDecision);
                if (!validDecision) activity?.SetStatus(ActivityStatusCode.Error, stop);
                logger?.LogInformation("Automation authorization classifier action={Action} catalog={CatalogKey} provider={ProviderAlias} model={ModelId} transport={Transport} effort={ReasoningEffort} maxOutputTokens={MaxOutputTokens} attempt={Attempt} maxAttempts={MaxAttempts} elapsedMs={ElapsedMs} terminal={Terminal} outputTokens={OutputTokens} validDecision={ValidDecision} traceId={TraceId}",
                    requestedAction, SafeMetadata(descriptor?.Key), SafeMetadata(descriptor?.ProviderAlias), SafeMetadata(descriptor?.ModelId),
                    descriptor?.Transport.ToString() ?? "Unknown", SafeMetadata(reasoningEffort), request.MaxOutputTokens, attempt, 2,
                    elapsed, stop, outputTokens, validDecision, Activity.Current?.TraceId.ToString());
            }
        }
        throw new UnreachableException();
    }

    private static string SafeMetadata(string? value) => value is null ? "Unknown" : new(value.Take(128).Where(c => !char.IsControl(c)).ToArray());

    public bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage) =>
        heuristic.IsScheduleConfirmation(currentUserText, conversationLanguage);

    private static bool TryParseDecision(string raw, out TriggerCommandAuthorizationDecision decision, out TriggerAuthorizationFailureReason reason)
    {
        decision = TriggerCommandAuthorizationDecision.Ambiguous;
        reason = TriggerAuthorizationFailureReason.InvalidDecision;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            using var document = JsonDocument.Parse(start >= 0 && end > start ? raw[start..(end + 1)] : raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count(property => property.NameEquals("decision")) != 1
                || !document.RootElement.TryGetProperty("decision", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var token = element.GetString()?.Trim().ToLowerInvariant();
            decision = token switch
            {
                "allow" => TriggerCommandAuthorizationDecision.Allow,
                "deny" => TriggerCommandAuthorizationDecision.Deny,
                "ambiguous" => TriggerCommandAuthorizationDecision.Ambiguous,
                _ => TriggerCommandAuthorizationDecision.Ambiguous
            };
            return token is "allow" or "deny" or "ambiguous";
        }
        catch (JsonException)
        {
            reason = TriggerAuthorizationFailureReason.MalformedJson;
            return false;
        }
    }

    private sealed record ClassifierRead(string Text, TriggerCommandAuthorizationVerdict? Failure, ModelCompleted? Terminal, ProviderErrorCode? ProviderCode = null);

    private static async Task<ClassifierRead> ReadTextAsync(
        ILanguageModel model,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        ModelCompleted? completed = null;
        await foreach (var item in model.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is ModelFailed failed)
                return new(string.Empty, failed.Failure.Code == ProviderErrorCode.InvalidResponse
                    ? new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.ProviderInvalidResponse)
                    : new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure), completed, failed.Failure.Code);
            if (item is ModelToolCallEvent)
                return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.InvalidResponseChannel), completed);
            if (completed is not null)
                return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.InvalidResponseChannel), completed);
            if (item is ModelCompleted terminal)
            {
                if (terminal.Reason != ModelStopReason.Completed)
                    return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput,
                        terminal.Reason == ModelStopReason.LengthLimit ? TriggerAuthorizationFailureReason.TruncatedOutput : TriggerAuthorizationFailureReason.InvalidResponseChannel), terminal);
                completed = terminal;
            }
            if (item is ModelTextDelta delta)
            {
                if (builder.Length + delta.Text.Length > 4096)
                    return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.OversizedOutput), completed);
                builder.Append(delta.Text);
            }
        }

        return new(builder.ToString(), completed is not null ? null : new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.IncompleteOutput), completed);
    }
}
