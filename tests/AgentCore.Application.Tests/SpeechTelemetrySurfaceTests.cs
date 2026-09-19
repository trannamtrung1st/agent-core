using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests;

[Collection("isolated-demo")]
public sealed class SpeechTelemetrySurfaceTests
{
    [Fact]
    public void Speech_instruments_are_provider_neutral_without_content()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var codes = new List<string>();
        var reasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != RuntimeTelemetry.Name)
            {
                return;
            }

            if (instrument.Name is SpeechTelemetry.PartialCountInstrument
                or SpeechTelemetry.PlaybackCompleteInstrument
                or SpeechTelemetry.CancelReasonInstrument
                or SpeechTelemetry.ErrorCodeInstrument
                or SpeechTelemetry.FinalLatencyInstrument
                or SpeechTelemetry.SegmentLatencyInstrument
                or SpeechTelemetry.PlaybackStartLatencyInstrument
                or SpeechTelemetry.VoiceSpeechFallbackInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? "";
                Assert.DoesNotContain("hello", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("pcm", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sk-", value, StringComparison.OrdinalIgnoreCase);
                if (tag.Key == "reason")
                {
                    reasons.Add(value);
                }

                if (tag.Key == "code")
                {
                    codes.Add(value);
                }
            }
        });
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            Assert.Empty(tags.ToArray());
        });
        listener.Start();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        SpeechTelemetry.RecordPartial();
        SpeechTelemetry.RecordFinalLatency(started);
        SpeechTelemetry.RecordSegmentLatency(started);
        SpeechTelemetry.RecordPlaybackStart(started);
        SpeechTelemetry.RecordPlaybackComplete();
        SpeechTelemetry.RecordCancel("userStop");
        SpeechTelemetry.RecordCancel("not-a-reason");
        SpeechTelemetry.RecordError("VoiceUnavailable");
        SpeechTelemetry.RecordVoiceSpeechFallback(SpeechTelemetry.VoiceSpeechFallbackReason.MissingExplicit);
        SpeechTelemetry.RecordVoiceSpeechFallback(SpeechTelemetry.VoiceSpeechFallbackReason.RejectedExplicit);
        listener.Dispose();

        var events = RuntimeTelemetry.SnapshotTimeline();
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.PartialCountInstrument);
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.FinalLatencyInstrument);
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.SegmentLatencyInstrument);
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.PlaybackStartLatencyInstrument);
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.PlaybackCompleteInstrument);
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.CancelReasonInstrument && item.Detail == "userStop");
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.CancelReasonInstrument && item.Detail == "other");
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.ErrorCodeInstrument && item.Detail == "VoiceUnavailable");
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.VoiceSpeechFallbackInstrument && item.Detail == "missingExplicit");
        Assert.Contains(events, item => item.Stage == SpeechTelemetry.VoiceSpeechFallbackInstrument && item.Detail == "rejectedExplicit");
        Assert.All(events, item =>
        {
            Assert.DoesNotContain("hello", item.Detail ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", item.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("userStop", reasons);
        Assert.Contains("other", reasons);
        Assert.Contains("VoiceUnavailable", codes);
        Assert.Equal(1, codes.Count(code => code == SpeechTelemetry.VoiceSpeechFallbackCode));
        Assert.Equal("stream=0,partial=0,bound=0,cancel=1", SpeechTelemetry.FormatCapabilities(new RecognitionCapabilities(false, false, false, true)));
        Assert.Equal("none", SpeechTelemetry.FormatCapabilities((SynthesisCapabilities?)null));
    }

    [Fact]
    public void Missing_explicit_fallback_is_not_an_error_code()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var codes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name
                && instrument.Name == SpeechTelemetry.ErrorCodeInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "code")
                {
                    codes.Add(tag.Value?.ToString() ?? "");
                }
            }
        });
        listener.Start();

        SpeechTelemetry.RecordVoiceSpeechFallback(SpeechTelemetry.VoiceSpeechFallbackReason.MissingExplicit);
        Assert.Empty(codes);
        SpeechTelemetry.RecordVoiceSpeechFallback(SpeechTelemetry.VoiceSpeechFallbackReason.RejectedExplicit);
        listener.Dispose();
        Assert.Single(codes, code => code == SpeechTelemetry.VoiceSpeechFallbackCode);
    }
}
