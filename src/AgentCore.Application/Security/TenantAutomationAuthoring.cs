using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Shared normalized construction for human and model authoring. Callers and the
// owning transaction independently establish actor, source and effect authority.
public static class TenantAutomationAuthoring
{
    public static Automation Build(TenantAgent agent, AgentDefinition policy, Automation? current, TenantAutomationChange change,
        TriggerProvenance provenance, DateTimeOffset at, IModelCatalog models, IIdGenerator ids, IEventFilterEvaluator? filters)
    {
        if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        if (current?.Status is AutomationStatus.Cancelled or AutomationStatus.Completed or AutomationStatus.Expired) throw new ArgumentException("A terminal Automation cannot be re-authored.");
        if (change.Enabled || current is null || current.ModelOverrideCatalogKey != change.ModelKey || current.ModelOverrideReasoningEffort != change.ReasoningEffort)
            ExecutionModelPolicy.RequireSelectable(models, change.ModelKey, change.ReasoningEffort);
        if (current is not null) TenantAutomationRules.Owner(current, agent);
        var children = Children(current, change, ids, filters);
        var proposed = new Automation(current?.AutomationId ?? ids.NewId(), TriggerOwner.ForTenant(agent.TenantId, agent.Instance.InstanceId, agent.PrincipalId),
            change.Enabled ? AutomationStatus.Active : AutomationStatus.Disabled, change.Instructions,
            children,
            NextOccurrence(current, children, change.Enabled, at), current?.ExpiresAtUtc, current?.OccurrenceCount ?? 0,
            (current?.Revision ?? 0) + 1, children.Max(t => t.Revision),
            provenance, null, change.ModelKey, change.ReasoningEffort,
            change.RequiresVision, change.Name, change.TargetSessionId is { } targetId ? AutomationExecutionTarget.Existing(targetId) : AutomationExecutionTarget.Background, change.ReportToSessionId is { } destination ? AutomationCompletionDelivery.ToSession(destination) : AutomationCompletionDelivery.None, change.RequiresTools);
        ValidateTransition(current, proposed, current?.Revision ?? 0, current is null ? "automations.create" : "automations.update", at);
        TenantAutomationRules.Configuration(proposed, policy, at, current: current);
        return proposed;
    }
    public static void ValidateTransition(Automation? current, Automation proposed, long expectedRevision, string permission, DateTimeOffset at)
    {
        AutomationRules.ValidateSave(current, proposed, expectedRevision);
        if (permission == "automations.delete")
        {
            if (current is null || !TenantAutomationRules.Same(current.WithCancellation(expectedRevision + 1, at), proposed))
                throw new ArgumentException("Cancellation must preserve the exact saved Automation and its history.");
            return;
        }
        if (permission != (current is null ? "automations.create" : "automations.update")
            || proposed.Status is not (AutomationStatus.Active or AutomationStatus.Disabled)
            || current?.Status is AutomationStatus.Completed or AutomationStatus.Expired
            || proposed.Provenance.UpdatedAt != at || current is null && proposed.Provenance.CreatedAt != at
            || proposed.OccurrenceCount != (current?.OccurrenceCount ?? 0) || proposed.SuspensionReason is not null
            || proposed.NextOccurrenceAtUtc != NextOccurrence(current, proposed.Triggers, proposed.Status == AutomationStatus.Active, at))
            throw new ArgumentException("Authoring must preserve accepted occurrence progress and select timing through the canonical schedule owner.");
    }
    private static DateTimeOffset? NextOccurrence(Automation? current, IReadOnlyList<AutomationTriggerRecord> children, bool enabled, DateTimeOffset at)
    {
        if (children.Count != 1 || children[0].Configuration is not ScheduleTrigger timing) return null;
        if (!enabled) return current?.IsSchedule == true ? current.NextOccurrenceAtUtc : null;
        // Keep already selected work on ordinary edits. Re-enabling or changing
        // timing deliberately selects the next future occurrence instead.
        if (current?.Status == AutomationStatus.Active && current.IsSchedule
            && current.Triggers[0].Configuration.SemanticEquals(timing)) return current.NextOccurrenceAtUtc;
        return TriggerScheduleCalculator.InitialNext(timing.Schedule, at);
    }
    private static IReadOnlyList<AutomationTriggerRecord> Children(Automation? current, TenantAutomationChange change, IIdGenerator ids, IEventFilterEvaluator? filters)
    {
        if ((change.Schedule is null) == (change.Events is null)) throw new ArgumentException("Use one Schedule or a bounded list of typed event subscriptions.");
        if (change.Schedule is { } timing)
        {
            var prior = current?.IsSchedule == true ? current.Triggers[0] : null; var next = new ScheduleTrigger(TenantAutomationRules.CanonicalSchedule(timing));
            return [new(prior?.TriggerId ?? ids.NewId(), next, true, prior is null ? 1 : prior.Revision + (prior.Configuration.SemanticEquals(next) && prior.Enabled ? 0 : 1))];
        }
        if (change.Events is not { Count: > 0 and <= TriggerLimits.MaxEventSubscriptions } events) throw new ArgumentException("Use 1–32 distinct event subscriptions.");
        var result = new List<AutomationTriggerRecord>();
        foreach (var input in events)
        {
            if (input.Configuration is not FilteredEventTrigger filtered) throw new ArgumentException("An event subscription requires a typed Webhook or built-in source.");
            if (filtered.FilterExpression is not null)
            {
                if (filters is null) throw new AccessDeniedException("event_filter_unavailable");
                if (filters.Validate(filtered.FilterExpression) is { } error) throw new ArgumentException(error);
            }
            AutomationTriggerRecord? prior = null;
            if (input.TriggerId is { } id)
            {
                prior = current?.Triggers.SingleOrDefault(t => t.TriggerId == id) ?? throw new SecurityConflictException("event_subscription_changed");
                if (prior.Revision != input.ExpectedRevision) throw new SecurityConflictException("stale_revision");
                var source = new AutomationTriggerRecord(prior.TriggerId, input.Configuration).Source;
                if (prior.Source != source) throw new ArgumentException("Replace a subscription with a new identity when changing its source.");
            }
            else if (input.ExpectedRevision != 0) throw new ArgumentException("New event subscriptions have no saved revision.");
            var changed = prior is not null && (!prior.Configuration.SemanticEquals(input.Configuration) || prior.Enabled != input.Enabled);
            result.Add(new(prior?.TriggerId ?? ids.NewId(), input.Configuration, input.Enabled, prior is null ? 1 : prior.Revision + (changed ? 1 : 0)));
        }
        // The Domain owner checks distinct child IDs/source locators and the full
        // serialized configuration budget; every child keeps its own revision.
        return result;
    }
}
