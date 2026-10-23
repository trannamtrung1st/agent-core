using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed class InstanceAutomationPolicy
{
    public async ValueTask<IReadOnlyList<InstanceAutomationPolicyChange>> PlanTenantAsync(Guid tenant, ITenantAutomationStore store, AgentInstance instance,
        AgentDefinition definition, DateTimeOffset now, CancellationToken ct)
    {
        var registrations = await store.ListFutureTenantAutomationsAsync(tenant, instance.InstanceId, 256, ct);
        if (registrations.Count == 256) throw Sessions.AgentCoreErrors.Validation("Too many registrations to reconcile safely in one configuration change.");
        return registrations.Select(registration => Change(registration, instance.Lifecycle == AgentInstanceLifecycle.Active
            && registration.Triggers.Where(t => t.Enabled).Any(t => OccurrenceCompatibility.Allows(definition, t.Configuration.Kind switch {
                AutomationTriggerKind.Event => TriggerSourceKind.ApplicationEvent,
                AutomationTriggerKind.CoreEvent => TriggerSourceKind.CoreEvent,
                _ => TriggerSourceKind.Schedule })), now)).ToArray();
    }
    private static InstanceAutomationPolicyChange Change(Automation registration, bool allowed, DateTimeOffset now)
    {
        var status = allowed ? AutomationStatus.Active : AutomationStatus.SuspendedPolicy;
        var next = registration.NextOccurrenceAtUtc;
        if (allowed && registration.Status == AutomationStatus.SuspendedPolicy && registration.Triggers.SingleOrDefault(t => t.Configuration.Kind == AutomationTriggerKind.Schedule)?.Configuration is ScheduleTrigger schedule)
            next ??= TriggerScheduleCalculator.InitialNext(schedule.Schedule, now);
        return new(registration.AutomationId, registration.Revision, status, next, allowed ? null : "Scheduling is disabled for this agent.");
    }
}
