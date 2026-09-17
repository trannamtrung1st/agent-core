using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Observability;

public static class InitiativeWaitTelemetry
{
    public const string InstrumentName = "initiative.next_wait_ms";

    private static readonly Histogram<double> NextWaitMs =
        RuntimeTelemetry.Meter.CreateHistogram<double>(InstrumentName);

    public static void Record(TimeSpan applied, string source, string clamp, SessionMode mode)
    {
        NextWaitMs.Record(
            applied.TotalMilliseconds,
            new TagList
            {
                { "source", source },
                { "clamp", clamp },
                { "mode", mode == SessionMode.Voice ? "voice" : "text" }
            });
    }

    public static void RecordIgnoredDeactivate(SessionMode mode) =>
        Record(TimeSpan.Zero, "none", "n/a", mode);
}
