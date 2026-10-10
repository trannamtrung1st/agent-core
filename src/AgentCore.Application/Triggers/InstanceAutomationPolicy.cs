using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

/// <summary>Every observed registration participates in CAS, including unchanged ones.</summary>
public sealed record InstanceAutomationPolicyChange(Guid AutomationId, long ExpectedRevision, AutomationStatus Status,
    DateTimeOffset? NextOccurrenceAtUtc, string? SuspensionReason);

public sealed class InstanceAutomationPolicy(ITriggerStore triggers, IMemoryStore profiles)
{
    public async ValueTask<IReadOnlyList<InstanceAutomationPolicyChange>> PlanAsync(AgentInstance instance,
        AgentDefinition definition, DateTimeOffset now, CancellationToken ct)
    {
        var registrations = await triggers.ListFutureRegistrationsForAgentInstanceAsync(instance.InstanceId, 256, ct);
        if (registrations.Count == 256) throw Sessions.AgentCoreErrors.Validation("Too many registrations to reconcile safely in one configuration change.");
        var result = new List<InstanceAutomationPolicyChange>();
        foreach (var registration in registrations)
        {
            var allowed = instance.Lifecycle == AgentInstanceLifecycle.Active
                && await profiles.LoadProfileAsync(registration.Owner.ProfileId, ct) is not null
                && OccurrenceCompatibility.Allows(definition, registration.Trigger.Kind switch {
                    AutomationTriggerKind.Event => TriggerSourceKind.ApplicationEvent,
                    AutomationTriggerKind.CoreEvent => TriggerSourceKind.CoreEvent,
                    _ => TriggerSourceKind.Schedule });
            var status = allowed ? AutomationStatus.Active : AutomationStatus.SuspendedPolicy;
            var next = registration.NextOccurrenceAtUtc;
            if (allowed && registration.Status == AutomationStatus.SuspendedPolicy && registration.Trigger is ScheduleTrigger schedule)
                next ??= TriggerScheduleCalculator.InitialNext(schedule.Schedule, now);
            result.Add(new(registration.AutomationId, registration.Revision, status, next,
                allowed ? null : "Scheduling is disabled for this agent."));
        }
        return result;
    }
}
