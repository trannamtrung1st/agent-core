using System.Diagnostics.Metrics;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class ProgressTelemetrySurfaceTests
{
    [Fact]
    public void Progress_instruments_tag_only_kind_and_state()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var eventTags = new List<(string Key, string Value)>();
        var durationTags = new List<(string Key, string Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != RuntimeTelemetry.Name)
            {
                return;
            }

            if (instrument.Name is ProgressTelemetry.EventInstrument or ProgressTelemetry.ActiveMsInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            Assert.Equal(ProgressTelemetry.EventInstrument, instrument.Name);
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? "";
                eventTags.Add((tag.Key, value));
                Assert.DoesNotContain("secret-token", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("call-1", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("internal planning", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("alpha", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Running tools", value, StringComparison.OrdinalIgnoreCase);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            Assert.Equal(ProgressTelemetry.ActiveMsInstrument, instrument.Name);
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? "";
                durationTags.Add((tag.Key, value));
                Assert.Equal("kind", tag.Key);
            }
        });
        listener.Start();

        ProgressTelemetry.Record(ResponseProgressKind.ReadingAttachments, ResponseProgressState.Started);
        ProgressTelemetry.Record(ResponseProgressKind.ReadingAttachments, ResponseProgressState.Completed, 12.5);
        ProgressTelemetry.Record(ResponseProgressKind.RunningTool, ResponseProgressState.Failed, 4);
        listener.Dispose();

        Assert.Contains(eventTags, tag => tag is ("kind", "readingAttachments"));
        Assert.Contains(eventTags, tag => tag is ("state", "started"));
        Assert.Contains(eventTags, tag => tag is ("state", "completed"));
        Assert.Contains(eventTags, tag => tag is ("kind", "runningTool"));
        Assert.Contains(eventTags, tag => tag is ("state", "failed"));
        Assert.All(eventTags, tag => Assert.True(tag.Key is "kind" or "state"));
        Assert.Equal(2, durationTags.Count);
        Assert.All(durationTags, tag => Assert.Equal("kind", tag.Key));
        Assert.Contains(durationTags, tag => tag.Value == "readingAttachments");
        Assert.Contains(durationTags, tag => tag.Value == "runningTool");

        var events = RuntimeTelemetry.SnapshotTimeline();
        Assert.Contains(
            events,
            item => item.Stage == ProgressTelemetry.EventInstrument && item.Detail == "readingAttachments:started");
        Assert.Contains(
            events,
            item => item.Stage == ProgressTelemetry.ActiveMsInstrument && item.Detail == "readingAttachments");
        Assert.All(
            events.Where(item => item.Stage.StartsWith("agent.progress", StringComparison.Ordinal)),
            item =>
            {
                Assert.DoesNotContain("secret", item.Detail ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("call-1", item.Detail ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain("operation", item.Detail ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(" ", item.Detail ?? "", StringComparison.Ordinal);
            });
    }
}
