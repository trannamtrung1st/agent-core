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
        if (ScheduleIntervalLanguage.LooksLikeIntervalCorrection(turn))
        {
            return draft;
        }

        if (!TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(text, language, scheduleContext))
        {
            return null;
        }

        if (HeuristicTriggerCommandAuthorizer.MatchesCreate(turn, language, scheduleContext, scheduleDraft: null))
        {
            return null;
        }

        return draft;
    }
}
