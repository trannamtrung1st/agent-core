using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Observability;

public static class SessionPauseTelemetry
{
    public const string AutoPausedInstrument = "session.auto_paused";
    public const string PauseReasonInstrument = "session.pause_reason";

    private static readonly Counter<long> AutoPaused =
        RuntimeTelemetry.Meter.CreateCounter<long>(AutoPausedInstrument);

    private static readonly Counter<long> PauseReason =
        RuntimeTelemetry.Meter.CreateCounter<long>(PauseReasonInstrument);

    public static void Record(string? pauseReason)
    {
        var reason = SessionPauseSemantics.CanonicalReason(pauseReason);
        PauseReason.Add(1, new TagList { { "reason", reason } });
        if (SessionPauseSemantics.IsAutomaticSemanticPause(reason))
        {
            AutoPaused.Add(1, new TagList { { "reason", reason } });
        }
    }
}
