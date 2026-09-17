using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentCore.Application.Observability;

public static class UserTextQueueTelemetry
{
    public const string BehaviorInstrument = "user_text.behavior";
    public const string QueuedInstrument = "user_text.queued";

    private static readonly Counter<long> Behavior =
        RuntimeTelemetry.Meter.CreateCounter<long>(BehaviorInstrument);

    private static readonly Counter<long> Queued =
        RuntimeTelemetry.Meter.CreateCounter<long>(QueuedInstrument);

    public static void Record(string behavior, bool queued)
    {
        Behavior.Add(1, new TagList { { "behavior", behavior } });
        if (queued)
        {
            Queued.Add(1, new TagList { { "behavior", "queue" } });
        }
    }
}

public static class ResponseCancelTelemetry
{
    public const string RequestedInstrument = "response.cancel.requested";
    public const string StaleInstrument = "response.cancel.stale";

    private static readonly Counter<long> Requested =
        RuntimeTelemetry.Meter.CreateCounter<long>(RequestedInstrument);

    private static readonly Counter<long> Stale =
        RuntimeTelemetry.Meter.CreateCounter<long>(StaleInstrument);

    public static void RecordRequested() => Requested.Add(1);

    public static void RecordStale() => Stale.Add(1);
}
