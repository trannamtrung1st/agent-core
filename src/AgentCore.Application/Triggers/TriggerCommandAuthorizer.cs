using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Triggers;

public enum TriggerCommandAuthorizationDecision
{
    Deny,
    Allow,
    Ambiguous,
    ClassifierUnavailable
}

public interface ITriggerCommandAuthorizer
{
    ValueTask<TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(
        string? currentUserText,
        string? conversationLanguage,
        TriggerCommandAction requestedAction,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default);

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
        return ScheduleContinuationLanguage.LooksScheduleRelated(text, conversationLanguage, scheduleContext);
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

    private static readonly Regex AgentDirectedCreate = new(
        @"\b(remind me|notify me|ping me|alert me|nudge me|wake me|set a reminder|schedule a\b|schedule\b.{0,80}\b(at|for|in)\b|every\s+\w+\s+remind|\bsay\b.{0,60}\bto me\b|\b(in|after)\s+\d+\s*(second|seconds|sec|secs|minute|minutes|min|mins|hour|hours|hr|hrs)\b.{0,40}\b(to me|me)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AgentDirectedCreateVi = new(
        @"(nhắc tôi|báo tôi|hẹn tôi|đặt lịch|lên lịch).{0,40}(phút|giờ|ngày|mai|thứ)|\d+\s*phút\s*nữa.{0,30}(chào|nhắc|báo)\s*tôi",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListIntent = new(
        @"\b(show|list|display)\b.{0,30}\b(my\s+)?(reminder|reminders|schedule|schedules)\b|\bwhat\b.{0,30}\b(reminder|reminders|scheduled)\b|\bwhat do i have scheduled\b|\bwhat'?s on my schedule\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListIntentVi = new(
        @"(xem|hiển thị|liệt kê).{0,20}(nhắc|lịch|hẹn)|(nhắc|lịch).{0,20}(của tôi|nào)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UpdateIntent = new(
        @"\b(move|reschedule|change|shift|push|delay|postpone|bump)\b.{0,30}\b(that|the|this|it|reminder|schedule)\b|\b(move|reschedule)\b.{0,20}\bto\b|\bmove it to\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CancelIntent = new(
        @"\b(cancel|delete|remove|drop|clear|stop)\b.{0,30}\b(that|the|this|it|reminder|schedule)\b|\bdelete that reminder\b|\bcancel that reminder\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NegatedCreate = new(
        @"\b(don'?t|do not|never)\b.{0,30}\b(create|remind|schedule|set)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        var allowed = requestedAction switch
        {
            TriggerCommandAction.Create => MatchesCreate(text, conversationLanguage, scheduleContext, scheduleDraft),
            TriggerCommandAction.List => MatchesList(text, conversationLanguage),
            TriggerCommandAction.Update => MatchesUpdate(text, conversationLanguage, scheduleContext),
            TriggerCommandAction.Cancel => MatchesCancel(text, conversationLanguage, scheduleContext),
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
        if (scheduleDraft is { IsActive: true }
            && ScheduleIntervalLanguage.LooksLikeIntervalCorrection(text))
        {
            return true;
        }

        if (NegatedCreate.IsMatch(text) || InformationalFuture.IsMatch(text) || CapabilityQuestion.IsMatch(text))
        {
            return false;
        }

        if (ScheduleContinuationLanguage.MatchesContinuationCreate(text, scheduleContext))
        {
            return true;
        }

        if (PrefersVietnamese(conversationLanguage) && AgentDirectedCreateVi.IsMatch(text))
        {
            return true;
        }

        return AgentDirectedCreate.IsMatch(text);
    }

    internal static bool MatchesList(string text, string? conversationLanguage) =>
        ListIntent.IsMatch(text) || (PrefersVietnamese(conversationLanguage) && ListIntentVi.IsMatch(text));

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
    HeuristicTriggerCommandAuthorizer heuristic) : ITriggerCommandAuthorizer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async ValueTask<TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(
        string? currentUserText,
        string? conversationLanguage,
        TriggerCommandAction requestedAction,
        ScheduleConversationContext? scheduleContext = null,
        ScheduleDraftContext? scheduleDraft = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return TriggerCommandAuthorizationDecision.Deny;
        }

        var text = HeuristicTriggerCommandAuthorizer.NormalizeTurn(currentUserText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return TriggerCommandAuthorizationDecision.Deny;
        }

        var heuristicDecision = await heuristic.AuthorizeCurrentTurnAsync(
            text,
            conversationLanguage,
            requestedAction,
            scheduleContext,
            scheduleDraft,
            cancellationToken).ConfigureAwait(false);
        if (heuristicDecision == TriggerCommandAuthorizationDecision.Allow)
        {
            return heuristicDecision;
        }

        var request = new ModelRequest(
            Guid.Empty,
            [
                new ModelMessage(ModelRole.System, BuildSystemPrompt(requestedAction, conversationLanguage, scheduleContext, scheduleDraft)),
                new ModelMessage(ModelRole.User, text)
            ],
            MaxOutputTokens: 64,
            Temperature: 0);

        var raw = await ReadTextAsync(languageModel, request, cancellationToken).ConfigureAwait(false);
        if (!TryParseDecision(raw, out var decision))
        {
            return TriggerCommandAuthorizationDecision.ClassifierUnavailable;
        }

        return decision;
    }

    public bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage) =>
        heuristic.IsScheduleConfirmation(currentUserText, conversationLanguage);

    private static string BuildSystemPrompt(
        TriggerCommandAction action,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext,
        ScheduleDraftContext? scheduleDraft = null)
    {
        var actionName = action switch
        {
            TriggerCommandAction.Create => "create a durable schedule or reminder",
            TriggerCommandAction.List => "list the user's schedules or reminders",
            TriggerCommandAction.Update => "update an existing schedule or reminder",
            TriggerCommandAction.Cancel => "cancel an existing schedule or reminder",
            _ => "perform a schedule management action"
        };

        var languageHint = string.IsNullOrWhiteSpace(conversationLanguage)
            || ConversationLanguagePolicy.IsAuto(conversationLanguage)
            ? "Interpret the user message in its own language."
            : $"The conversation language is {conversationLanguage}. Interpret the user message accordingly.";

        var referent = scheduleContext is { IsReferentAvailable: true }
            ? string.Join('\n', scheduleContext.ToPromptLines())
            : "Trusted schedule referent: (none)";

        var draft = scheduleDraft is { IsActive: true }
            ? string.Join('\n', scheduleDraft.ToPromptLines())
            : "Schedule draft (clarification only): (none)";

        const string jsonHint =
            "Reply with JSON only: {\"decision\":\"allow\"} or {\"decision\":\"deny\"} or {\"decision\":\"ambiguous\"}.";
        return $"""
            You classify whether the user's current message explicitly requests or authorizes {actionName}.
            The trusted schedule referent may only resolve references such as "another", "that", "it", or "same".
            It does not independently grant authority. The current user message must itself request or continue the action.
            {referent}
            {draft}
            The schedule draft may only resolve omitted schedule details from a prior failed attempt; it does not independently grant authority.
            {languageHint}
            {jsonHint}
            Use allow when the current message explicitly continues or requests the proposed action, including elliptical continuations when a referent exists.
            Use deny when the message is unrelated, informational, a question about capability, or about past assistant behavior.
            Use ambiguous only when the current message is schedule-related but still unclear even with the referent.
            """;
    }

    private static bool TryParseDecision(string raw, out TriggerCommandAuthorizationDecision decision)
    {
        decision = TriggerCommandAuthorizationDecision.Ambiguous;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("decision", out var element))
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
            return false;
        }
    }

    private static async Task<string> ReadTextAsync(
        ILanguageModel model,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        await foreach (var item in model.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is ModelTextDelta delta)
            {
                builder.Append(delta.Text);
            }
        }

        return builder.ToString();
    }
}
