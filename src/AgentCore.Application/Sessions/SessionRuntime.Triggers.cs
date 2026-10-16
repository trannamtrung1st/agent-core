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
            _scheduleDraftEligibleForNextUserTurn = _scheduleDraftContext is not null;
            return;
        }

        _scheduleDraftContext = null;
        _scheduleDraftEligibleForNextUserTurn = false;
        _scheduleConversationContext = ScheduleConversationContext.RefreshFromRegistrationJson(
            _scheduleConversationContext, resultText, action);
    }

    private void RefreshScheduleDraftForUserTurn()
    {
        _scheduleDraftContext = ScheduleDraftAdmission.PrepareDraftForUserTurn(
            _scheduleDraftContext,
            ref _scheduleDraftEligibleForNextUserTurn);
    }
}
