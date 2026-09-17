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

    public const string PendingBatchSizeInstrument = "user_text.pending_batch_size";
    public const string PendingBatchStartedInstrument = "user_text.pending_batch_started";

    private static readonly Histogram<int> PendingBatchSize =
        RuntimeTelemetry.Meter.CreateHistogram<int>(PendingBatchSizeInstrument);

    private static readonly Counter<long> PendingBatchStarted =
        RuntimeTelemetry.Meter.CreateCounter<long>(PendingBatchStartedInstrument);

    public static void Record(string behavior, bool queued)
    {
        Behavior.Add(1, new TagList { { "behavior", behavior } });
        if (queued)
        {
            Queued.Add(1, new TagList { { "behavior", "queue" } });
        }
    }

    public static void RecordPendingBatchStarted(int size)
    {
        var bounded = Math.Clamp(size, 1, 8);
        PendingBatchSize.Record(bounded);
        PendingBatchStarted.Add(1);
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
