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

        var refreshed = ScheduleConversationContext.TryFromRegistrationJson(resultText, action);
        if (refreshed is not null)
        {
            _scheduleConversationContext = refreshed;
        }
    }
}
