using System.Globalization;
using System.Text.RegularExpressions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Triggers;

public interface ITriggerCommandAuthorizer
{
    TriggerCommandAction AuthorizeCurrentTurn(string? currentUserText, string? conversationLanguage);

    bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage);
}

public sealed class HeuristicTriggerCommandAuthorizer : ITriggerCommandAuthorizer
{
    private static readonly Regex TestHarnessMarker = new(
        @"\[test:[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RelativeTime = new(
        @"\b(in\s+\d+|after\s+\d+|\d+\s*(min|mins|minute|minutes|hour|hours|hr|hrs|day|days))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ClockOrCalendar = new(
        @"\b(tomorrow|today|tonight|next\s+\w+|at\s+\d|@\s*\d|\d{1,2}\s*:\s*\d{2}|\d{1,2}\s*(am|pm)|every\s+\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Delegation = new(
        @"\b(remind|schedule|notify|ping|alert|nudge|say|tell|message|check\s+in|wake\s+me)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListIntent = new(
        @"\b(what|which|show|list|display|see)\b.{0,40}\b(reminder|reminders|schedule|schedules)\b|\b(my|all)\s+(reminder|reminders|schedule|schedules)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UpdateIntent = new(
        @"\b(move|reschedule|change|shift|push|delay|postpone|bump)\b.{0,30}\b(that|the|this|it|reminder|schedule)\b|\b(move|reschedule)\b.{0,20}\bto\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CancelIntent = new(
        @"\b(cancel|delete|remove|drop|clear|stop)\b.{0,30}\b(that|the|this|it|reminder|schedule)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Negation = new(
        @"\b(don'?t|do\s+not|never|no\s+need\s+to|without)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NegatedCreate = new(
        @"\b(don'?t|do\s+not|never)\b.{0,30}\b(create|remind|schedule|set)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public TriggerCommandAction AuthorizeCurrentTurn(string? currentUserText, string? conversationLanguage)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return TriggerCommandAction.None;
        }

        var text = TestHarnessMarker.Replace(currentUserText.Trim(), string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return TriggerCommandAction.None;
        }

        var actions = TriggerCommandAction.None;
        if (ListIntent.IsMatch(text))
        {
            actions |= TriggerCommandAction.List;
        }

        if (UpdateIntent.IsMatch(text))
        {
            actions |= TriggerCommandAction.Update;
        }

        if (CancelIntent.IsMatch(text))
        {
            actions |= TriggerCommandAction.Cancel;
        }

        var wantsCreate = Delegation.IsMatch(text) || RelativeTime.IsMatch(text) || ClockOrCalendar.IsMatch(text);
        if (wantsCreate && !NegatedCreate.IsMatch(text) && !(Negation.IsMatch(text) && Delegation.IsMatch(text)))
        {
            actions |= TriggerCommandAction.Create;
        }

        return actions;
    }

    public bool IsScheduleConfirmation(string? currentUserText, string? conversationLanguage)
    {
        if (string.IsNullOrWhiteSpace(currentUserText))
        {
            return false;
        }

        var trimmed = TestHarnessMarker.Replace(currentUserText.Trim(), string.Empty).Trim();
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

        if (ConversationLanguagePolicy.IsAuto(conversationLanguage ?? ConversationLanguagePolicy.Auto))
        {
            return VietnameseConfirmation.IsMatch(trimmed);
        }

        if (string.Equals(conversationLanguage, "vi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conversationLanguage, "vi-VN", StringComparison.OrdinalIgnoreCase))
        {
            return VietnameseConfirmation.IsMatch(trimmed);
        }

        return false;
    }

    private static readonly Regex VietnameseConfirmation = new(
        @"^(vâng|ừ|uh|uhm|đồng ý|ok|oke|được|làm đi|xác nhận)(\s+.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
