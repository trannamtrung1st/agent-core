namespace AgentCore.Application.Triggers;

public static class ScheduleDraftAdmission
{
    public static ScheduleDraftContext? PrepareDraftForUserTurn(
        ScheduleDraftContext? draft,
        ref bool eligibleForNextUserTurn)
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

        // The next turn receives the retained draft as untrusted context only.
        // It never supplies omitted arguments or grants authority.
        return draft;
    }
}
