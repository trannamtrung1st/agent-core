using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

public sealed class AgentRunTelemetryTests
{
    [Fact]
    public void Execution_metrics_accept_only_bounded_outcomes_and_never_payload_tags()
    {
        var values = new ConcurrentBag<(string Name, string Key, string Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => {
            if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name is "agent_run_events" or "activation_events" or "background_session_events")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => {
            foreach (var tag in tags) values.Add((instrument.Name, tag.Key, tag.Value?.ToString() ?? ""));
        });
        listener.Start();
        RuntimeTelemetry.RecordActivation("admitted"); RuntimeTelemetry.RecordActivation("duplicate");
        RuntimeTelemetry.RecordAgentRun("retry"); RuntimeTelemetry.RecordAgentRun("unconfirmed-effect");
        RuntimeTelemetry.RecordBackgroundSession("completion-skipped");
        RuntimeTelemetry.RecordAgentRun("SECRET_CHECKPOINT"); RuntimeTelemetry.RecordActivation(Guid.NewGuid().ToString());
        RuntimeTelemetry.RecordBackgroundSession("SECRET_APPROVAL_BODY");
        Assert.Contains(values, value => value.Name == "agent_run_events" && value.Value == "retry");
        Assert.Contains(values, value => value.Name == "activation_events" && value.Value == "duplicate");
        Assert.Contains(values, value => value.Name == "background_session_events" && value.Value == "completion-skipped");
        Assert.All(values, value => { Assert.Equal("outcome", value.Key); Assert.DoesNotContain("SECRET", value.Value); Assert.True(value.Value.Length < 32); });
    }
}
