namespace AgentCore.Application.Sessions;

public static class SessionPauseSemantics
{
    public static bool RequiresExplicitResume(string? pauseReason) =>
        pauseReason is not ("disconnected" or "recovered");

    public static bool IsTransportResumable(string? pauseReason) =>
        pauseReason is "disconnected" or "recovered";
}
