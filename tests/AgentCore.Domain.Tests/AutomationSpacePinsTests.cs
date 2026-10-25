using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Tests;

public sealed class AutomationSpacePinsTests
{
    [Fact]
    public void Saved_and_occurrence_pins_are_closed_and_survive_canonical_lifecycle_copies()
    {
        var owner = TriggerOwner.ForTenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var space = Guid.NewGuid(); var environment = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        Automation Saved(Guid? selectedSpace, Guid? selectedEnvironment) => new(Guid.NewGuid(), owner, AutomationStatus.Active, "Exact task",
            new ScheduleTrigger(new OneShotSchedule(at.AddMinutes(5), "UTC")), at.AddMinutes(5), null, 0, 1, 1,
            new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null, pinnedSpaceId: selectedSpace, pinnedEnvironmentId: selectedEnvironment);
        Assert.Throws<ArgumentException>(() => Saved(null, environment));
        Assert.Throws<ArgumentException>(() => Saved(Guid.Empty, null));
        var saved = Saved(space, environment);
        var consent = new JsonSerializerOptions(); consent.Converters.Add(new AgentCore.Domain.Security.PreparedAutomationJsonConverter());
        var frozen = JsonSerializer.Serialize(saved, consent);
        Assert.Contains("\"SchemaVersion\":5", frozen);
        var restored = JsonSerializer.Deserialize<Automation>(frozen, consent)!;
        Assert.Equal(space, restored.PinnedSpaceId); Assert.Equal(environment, restored.PinnedEnvironmentId);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Automation>(frozen.Replace("\"SchemaVersion\":5", "\"SchemaVersion\":4", StringComparison.Ordinal), consent));
        foreach (var copy in new[] { saved.WithScheduleAdvance(AutomationStatus.Completed, null, 1, 2, at, null),
            saved.WithCancellation(2, at), saved.WithModelOverride("scripted", null, 2, at),
            saved.WithUpdate("Updated task", saved.Trigger!, at.AddMinutes(10), null, 2, 2, at) })
        { Assert.Equal(space, copy.PinnedSpaceId); Assert.Equal(environment, copy.PinnedEnvironmentId); }
        var occurrence = new TriggerOccurrence(Guid.NewGuid(), "space-pin-fixture", saved.AutomationId, owner, TriggerSourceKind.Schedule,
            at, at, at, "{}", null, 1, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
            pinnedSpaceId: space, pinnedEnvironmentId: environment);
        occurrence = occurrence.WithAdmissionEvidence("{\"source\":\"frozen\"}").WithModelPin(new("scripted", "synthetic", "scripted", null, ExecutionModelSource.UnattendedDefault));
        occurrence = occurrence.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, null, 1, at, null, null)
            .WithExecutionAcceptance(Guid.NewGuid(), Guid.NewGuid(), 1, at);
        var reopened = JsonSerializer.Deserialize<TriggerOccurrence>(JsonSerializer.Serialize(occurrence))!;
        Assert.Equal(space, reopened.PinnedSpaceId); Assert.Equal(environment, reopened.PinnedEnvironmentId);
        Assert.Equal(occurrence.AcceptedAgentRunId, reopened.AcceptedAgentRunId); Assert.Equal(occurrence.EvidenceJson, reopened.EvidenceJson);
    }
}
