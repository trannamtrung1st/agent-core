using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Tests;

public sealed class TaskTelemetryTests
{
    [Fact]
    public void Cooperation_telemetry_excludes_authored_content_and_unbounded_reason_labels()
    {
        const string secret = "PRIVATE authored objective / message / display name";
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) => { if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name.StartsWith("agent_messages.")) target.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => { lock (measurements) measurements.Add(tags.ToArray()); });
        listener.Start();
        var at = DateTimeOffset.UtcNow;
        var message = new AgentMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, TaskActorKind.Agent,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AgentMessageKind.Note,
            secret, "telemetry-request", new string('a', 64), at);
        var receipt = new AgentMessageReceipt(message.TenantId, message.MessageId, message.RecipientInstanceId,
            AgentMessageDelivery.Undeliverable, 2, at, Reason: secret);
        using var trace = new Activity("cooperation-privacy-test").Start();
        TaskTelemetry.Message(AgentMessageEvent.WakeWithheld, message, receipt);
        Assert.Contains(measurements, tags => tags.Any(t => t.Key == "event" && Equals(t.Value, "WakeWithheld")));
        Assert.All(measurements.SelectMany(t => t), tag => {
            Assert.DoesNotContain(secret, tag.Value?.ToString() ?? "", StringComparison.Ordinal);
            Assert.False(Guid.TryParse(tag.Value?.ToString(), out _));
        });
        var observed = Assert.Single(trace.Events);
        Assert.Contains(observed.Tags, tag => tag.Key == "message.id" && Equals(tag.Value, message.MessageId));
        Assert.All(observed.Tags, tag => Assert.DoesNotContain(secret, tag.Value?.ToString() ?? "", StringComparison.Ordinal));
        Assert.Contains(observed.Tags, tag => tag.Key == "reason" && Equals(tag.Value, "other"));
    }
}
