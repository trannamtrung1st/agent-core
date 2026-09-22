using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("isolated-demo")]
public sealed class UserTextQueueTelemetryTests
{
    [Fact]
    public void Queue_and_cancel_meters_stay_low_cardinality()
    {
        var gate = new object();
        var behaviors = new List<string>();
        var sizes = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != RuntimeTelemetry.Name)
            {
                return;
            }

            if (instrument.Name is UserTextQueueTelemetry.BehaviorInstrument
                or UserTextQueueTelemetry.QueuedInstrument
                or UserTextQueueTelemetry.PendingBatchStartedInstrument
                or ResponseCancelTelemetry.RequestedInstrument
                or ResponseCancelTelemetry.StaleInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }

            if (instrument.Name == UserTextQueueTelemetry.PendingBatchSizeInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (gate)
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "behavior")
                    {
                        behaviors.Add(tag.Value?.ToString() ?? "");
                    }
                    else
                    {
                        Assert.NotEqual("user-text", tag.Key);
                        Assert.DoesNotContain("hello", tag.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("notes.txt", tag.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        });
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            lock (gate)
            {
                Assert.Empty(tags.ToArray());
                sizes.Add(value);
            }
        });
        listener.Start();

        lock (gate)
        {
            UserTextQueueTelemetry.Record("queue", queued: true);
            UserTextQueueTelemetry.Record("interrupt", queued: false);
            UserTextQueueTelemetry.RecordPendingBatchStarted(99);
            ResponseCancelTelemetry.RecordRequested();
            ResponseCancelTelemetry.RecordStale();
        }

        listener.Dispose();

        lock (gate)
        {
            var capturedBehaviors = behaviors.ToArray();
            Assert.Contains("queue", capturedBehaviors);
            Assert.Contains("interrupt", capturedBehaviors);
            Assert.All(capturedBehaviors, value => Assert.Contains(value, new[] { "queue", "interrupt" }));
            Assert.Contains(8, sizes.ToArray());
            Assert.All(sizes.ToArray(), value => Assert.InRange(value, 1, 8));
        }
    }
}
