using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class EmailTelemetryTests
{
    [Fact]
    public void Email_metrics_bound_every_label_and_keep_identity_and_content_out()
    {
        var tags = new ConcurrentBag<(string Instrument, string Key, object? Value)>();
        var durations = new ConcurrentBag<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name is "email.operations" or "email.operation.duration_ms")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, labels, _) =>
        { foreach (var label in labels) tags.Add((instrument.Name, label.Key, label.Value)); });
        listener.SetMeasurementEventCallback<double>((instrument, value, labels, _) =>
        { durations.Add(value); foreach (var label in labels) tags.Add((instrument.Name, label.Key, label.Value)); });
        listener.Start();
        RuntimeTelemetry.RecordEmailOperation("gmail", "oauth.refresh", "reconnect_required", false, 5);
        RuntimeTelemetry.RecordEmailOperation("private@example.test", "code=private-code", Guid.NewGuid().ToString(), true, double.NaN);
        Assert.Contains(tags, t => t.Instrument == "email.operations" && t.Key == "outcome" && Equals(t.Value, "reconnect_required"));
        Assert.Contains(tags, t => t.Key == "profile" && Equals(t.Value, "synthetic"));
        Assert.Contains(0, durations); Assert.Contains(5, durations);
        Assert.All(tags, t =>
        {
            Assert.Contains(t.Key, new[] { "provider", "operation", "outcome", "profile" });
            Assert.DoesNotContain("private", t.Value?.ToString());
            Assert.False(Guid.TryParse(t.Value?.ToString(), out _));
        });
        Assert.DoesNotContain(RuntimeTelemetry.SnapshotTimeline().Where(t => t.Stage == "email"), t => t.Detail!.Contains("private", StringComparison.Ordinal));
    }
}
