using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private void ApplyScheduleConversationFromTool(string toolName, string resultText)
    {
        var action = TriggerAuthorization.ActionForTool(toolName);
        if (action == TriggerCommandAction.None)
        {
            return;
        }

        if (resultText.Contains("\"error\"", StringComparison.Ordinal))
        {
            _scheduleDraftContext = ScheduleDraftContext.TryFromToolError(resultText);
            return;
        }

        _scheduleDraftContext = null;
        var refreshed = ScheduleConversationContext.TryFromRegistrationJson(resultText, action);
        if (refreshed is not null)
        {
            _scheduleConversationContext = refreshed;
        }
    }

    private void RefreshScheduleDraftForUserTurn(string? text, string? language)
    {
        if (_scheduleDraftContext is null)
        {
            return;
        }

        if (!TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(text, language, _scheduleConversationContext))
        {
            _scheduleDraftContext = null;
            return;
        }

        var turn = text ?? string.Empty;
        if (HeuristicTriggerCommandAuthorizer.MatchesCreate(turn, language, _scheduleConversationContext, scheduleDraft: null)
            && !ScheduleIntervalLanguage.LooksLikeIntervalCorrection(turn))
        {
            _scheduleDraftContext = null;
        }
    }
}
