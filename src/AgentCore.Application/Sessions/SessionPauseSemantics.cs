using AgentCore.Application.Observability;

namespace AgentCore.Application.Sessions;

public static class SessionPauseSemantics
{
    public static bool RequiresExplicitResume(string? pauseReason) =>
        pauseReason is not ("disconnected" or "recovered");

    public static bool IsTransportResumable(string? pauseReason) =>
        pauseReason is "disconnected" or "recovered";

    public static bool IsAutomaticSemanticPause(string? pauseReason) =>
        pauseReason is "inactivity" or "silentEvaluation" or "initiative";

    public static string CanonicalReason(string? pauseReason) => pauseReason switch
    {
        "manual" or "inactivity" or "silentEvaluation" or "initiative"
            or "disconnected" or "recovered" or "persistence" => pauseReason,
        _ => "other"
    };
}
