namespace AgentCore.Application.Triggers;

public static class ScheduleDraftAdmission
{
    public static ScheduleDraftContext? PrepareDraftForUserTurn(
        ScheduleDraftContext? draft,
        ref bool eligibleForNextUserTurn,
        string? text,
        string? language,
        ScheduleConversationContext? scheduleContext)
    {
        if (draft is null)
        {
            return null;
        }

        if (!eligibleForNextUserTurn)
        {
            return null;
        }

        eligibleForNextUserTurn = false;

        var turn = text ?? string.Empty;
        if (ScheduleIntervalLanguage.IsIntervalOnlyCorrection(turn, language))
        {
            return draft;
        }

        // Only an interval-only next turn can carry omitted task details forward.
        return null;
    }
}
