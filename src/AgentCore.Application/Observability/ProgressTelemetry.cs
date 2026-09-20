using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Events;

namespace AgentCore.Application.Observability;

public static class ProgressTelemetry
{
    public const string EventInstrument = "agent.progress.event";
    public const string ActiveMsInstrument = "agent.progress.active_ms";

    private static readonly Counter<long> Events =
        RuntimeTelemetry.Meter.CreateCounter<long>(EventInstrument);

    private static readonly Histogram<double> ActiveMs =
        RuntimeTelemetry.Meter.CreateHistogram<double>(ActiveMsInstrument);

    public static void Record(ResponseProgressKind kind, ResponseProgressState state, double? activeMs = null)
    {
        var kindWire = ToKind(kind);
        var stateWire = ToState(state);
        Events.Add(1, new TagList { { "kind", kindWire }, { "state", stateWire } });
        RuntimeTelemetry.RecordDiagnostic(EventInstrument, 0, $"{kindWire}:{stateWire}");
        if (activeMs is not double milliseconds)
        {
            return;
        }

        ActiveMs.Record(milliseconds, new TagList { { "kind", kindWire } });
        RuntimeTelemetry.RecordDiagnostic(ActiveMsInstrument, milliseconds, kindWire);
    }

    public static string ToKind(ResponseProgressKind kind) => kind switch
    {
        ResponseProgressKind.Preparing => "preparing",
        ResponseProgressKind.ReadingAttachments => "readingAttachments",
        ResponseProgressKind.RunningTool => "runningTool",
        ResponseProgressKind.WaitingExternal => "waitingExternal",
        ResponseProgressKind.Finalizing => "finalizing",
        _ => "other"
    };

    public static string ToState(ResponseProgressState state) => state switch
    {
        ResponseProgressState.Started => "started",
        ResponseProgressState.Updated => "updated",
        ResponseProgressState.Completed => "completed",
        ResponseProgressState.Failed => "failed",
        _ => "other"
    };
}
