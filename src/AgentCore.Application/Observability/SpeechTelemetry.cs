using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Observability;

public static class SpeechTelemetry
{
    public const string PartialCountInstrument = "speech.partial.count";
    public const string FinalLatencyInstrument = "speech.final.latency";
    public const string SegmentLatencyInstrument = "speech.segment.latency";
    public const string PlaybackStartLatencyInstrument = "speech.playback.start.latency";
    public const string PlaybackCompleteInstrument = "speech.playback.complete";
    public const string CancelReasonInstrument = "speech.cancel.reason";
    public const string ErrorCodeInstrument = "speech.error.code";
    public const string VoiceSpeechFallbackInstrument = "speech.voice.fallback";

    private static readonly Counter<long> PartialCount =
        RuntimeTelemetry.Meter.CreateCounter<long>(PartialCountInstrument);

    private static readonly Histogram<double> FinalLatency =
        RuntimeTelemetry.Meter.CreateHistogram<double>(FinalLatencyInstrument);

    private static readonly Histogram<double> SegmentLatency =
        RuntimeTelemetry.Meter.CreateHistogram<double>(SegmentLatencyInstrument);

    private static readonly Histogram<double> PlaybackStartLatency =
        RuntimeTelemetry.Meter.CreateHistogram<double>(PlaybackStartLatencyInstrument);

    private static readonly Counter<long> PlaybackComplete =
        RuntimeTelemetry.Meter.CreateCounter<long>(PlaybackCompleteInstrument);

    private static readonly Counter<long> CancelReason =
        RuntimeTelemetry.Meter.CreateCounter<long>(CancelReasonInstrument);

    private static readonly Counter<long> ErrorCode =
        RuntimeTelemetry.Meter.CreateCounter<long>(ErrorCodeInstrument);

    private static readonly Counter<long> VoiceSpeechFallback =
        RuntimeTelemetry.Meter.CreateCounter<long>(VoiceSpeechFallbackInstrument);

    private static readonly HashSet<string> CancelReasons =
    [
        "userStop",
        "userBargeIn",
        "newText",
        "disconnected",
        "modeChange",
        "ended",
        "deactivated"
    ];

    public static void RecordPartial()
    {
        PartialCount.Add(1);
        RuntimeTelemetry.RecordDiagnostic(PartialCountInstrument, 0, null);
    }

    public static void RecordFinalLatency(long startedTimestamp)
    {
        var elapsed = RuntimeTelemetry.ElapsedMs(startedTimestamp);
        FinalLatency.Record(elapsed);
        RuntimeTelemetry.RecordDiagnostic(FinalLatencyInstrument, elapsed, null);
    }

    public static void RecordSegmentLatency(long startedTimestamp)
    {
        var elapsed = RuntimeTelemetry.ElapsedMs(startedTimestamp);
        SegmentLatency.Record(elapsed);
        RuntimeTelemetry.RecordDiagnostic(SegmentLatencyInstrument, elapsed, null);
    }

    public static void RecordPlaybackStart(long startedTimestamp)
    {
        var elapsed = RuntimeTelemetry.ElapsedMs(startedTimestamp);
        PlaybackStartLatency.Record(elapsed);
        RuntimeTelemetry.RecordDiagnostic(PlaybackStartLatencyInstrument, elapsed, null);
    }

    public static void RecordPlaybackComplete()
    {
        PlaybackComplete.Add(1);
        RuntimeTelemetry.RecordDiagnostic(PlaybackCompleteInstrument, 0, null);
    }

    public static void RecordCancel(string reason)
    {
        var safe = CancelReasons.Contains(reason) ? reason : "other";
        CancelReason.Add(1, new TagList { { "reason", safe } });
        RuntimeTelemetry.RecordDiagnostic(CancelReasonInstrument, 0, safe);
    }

    public const string VoiceSpeechFallbackCode = "VoiceSpeechFallback";

    public enum VoiceSpeechFallbackReason
    {
        MissingExplicit,
        RejectedExplicit
    }

    public static void RecordVoiceSpeechFallback(VoiceSpeechFallbackReason reason)
    {
        var wire = reason switch
        {
            VoiceSpeechFallbackReason.MissingExplicit => "missingExplicit",
            VoiceSpeechFallbackReason.RejectedExplicit => "rejectedExplicit",
            _ => "other"
        };

        VoiceSpeechFallback.Add(1, new TagList { { "reason", wire } });
        RuntimeTelemetry.RecordDiagnostic(VoiceSpeechFallbackInstrument, 0, wire);
        if (reason == VoiceSpeechFallbackReason.RejectedExplicit)
        {
            RecordError(VoiceSpeechFallbackCode);
        }
    }

    public static void RecordError(string code)
    {
        var safe = string.IsNullOrWhiteSpace(code) ? "Unknown" : code.Trim();
        if (safe.Length > 64)
        {
            safe = safe[..64];
        }

        ErrorCode.Add(1, new TagList { { "code", safe } });
        RuntimeTelemetry.RecordDiagnostic(ErrorCodeInstrument, 0, safe);
    }

    public static string FormatCapabilities(RecognitionCapabilities? capabilities) =>
        capabilities is null
            ? "none"
            : $"stream={(capabilities.StreamingAudio ? 1 : 0)},partial={(capabilities.PartialTranscripts ? 1 : 0)},bound={(capabilities.SpeechBoundaryEvents ? 1 : 0)},cancel={(capabilities.Cancellation ? 1 : 0)}";

    public static string FormatCapabilities(SynthesisCapabilities? capabilities) =>
        capabilities is null
            ? "none"
            : $"stream={(capabilities.StreamingAudio ? 1 : 0)},marks={(capabilities.TimingMarks ? 1 : 0)},cancel={(capabilities.Cancellation ? 1 : 0)}";
}
