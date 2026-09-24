using System.Text.RegularExpressions;

namespace AgentCore.Application.Triggers;

public static partial class ScheduleContinuationLanguage
{
    public static bool MatchesContinuationCreate(string text, ScheduleConversationContext? context) =>
        context is { IsReferentAvailable: true } && ContinuationCreate().IsMatch(text);

    public static bool MatchesContinuationUpdate(string text, ScheduleConversationContext? context) =>
        context is { IsReferentAvailable: true }
        && (ReferentUpdate().IsMatch(text)
            || ReferentMakeThat().IsMatch(text)
            || ReferentStopAfter().IsMatch(text)
            || HeuristicTriggerCommandAuthorizer.MatchesUpdate(text, null));

    public static bool MatchesContinuationCancel(string text, ScheduleConversationContext? context) =>
        context is { IsReferentAvailable: true }
        && (ReferentCancel().IsMatch(text) || HeuristicTriggerCommandAuthorizer.MatchesCancel(text, null));

    public static bool LooksScheduleRelated(
        string text,
        string? conversationLanguage,
        ScheduleConversationContext? context) =>
        HeuristicTriggerCommandAuthorizer.MatchesCreate(text, conversationLanguage, context)
        || HeuristicTriggerCommandAuthorizer.MatchesList(text, conversationLanguage)
        || HeuristicTriggerCommandAuthorizer.MatchesUpdate(text, conversationLanguage, context)
        || HeuristicTriggerCommandAuthorizer.MatchesCancel(text, conversationLanguage, context);

    [GeneratedRegex(
        @"\banother\b(?:\s+(?:one|reminder|schedule))?(?:\s+at\b|\s+for\b|\s+tomorrow\b|\s+on\b|\s+in\b|\s+after\b|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationCreate();

    [GeneratedRegex(
        @"\b(move|reschedule|change|shift|push|delay|postpone|bump)\b.{0,20}\b(that|it|the one|this one)\b|\bwhat about\b.{0,20}\b(instead|at)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferentUpdate();

    [GeneratedRegex(
        @"\b(cancel|delete|remove|drop|clear|stop)\b.{0,20}\b(that|it|the one|this one)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferentCancel();

    [GeneratedRegex(
        @"\bmake\b.{0,15}\bthat\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferentMakeThat();

    [GeneratedRegex(
        @"\bstop\b.{0,20}\bafter\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferentStopAfter();
}
