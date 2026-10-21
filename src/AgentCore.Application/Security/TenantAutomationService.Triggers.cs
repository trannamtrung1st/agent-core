using AgentCore.Domain.Triggers;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Security;

public sealed partial class TenantAutomationService
{
    public async ValueTask<AgentCore.Domain.Events.EventFilterResult> TestFilterAsync(Guid tenant, Guid instance,
        HumanSessionReference human, Guid? automation, string? expression, System.Text.Json.JsonElement sample, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct).ConfigureAwait(false);
        await AgentRead(state, instance, human.PrincipalId, ct).ConfigureAwait(false);
        if (automation is { } id)
        {
            var saved = await ReadAsync(tenant, id, human, ct).ConfigureAwait(false);
            if (saved.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
            await Require(state, human.PrincipalId, "automations.update", TenantAutomationRules.Scope(tenant, id), ct).ConfigureAwait(false);
        }
        else await Require(state, human.PrincipalId, "automations.create", AgentCore.Domain.Security.AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (filters is null) throw new AccessDeniedException("event_filter_unavailable");
        var result = sample.ValueKind != System.Text.Json.JsonValueKind.Object || !sample.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != System.Text.Json.JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            ? new AgentCore.Domain.Events.EventFilterResult(null, "error", "filter-envelope-schema") : filters.Evaluate(expression, sample, ct);
        await Unchanged(state, human, ct).ConfigureAwait(false); return result;
    }
    private IReadOnlyList<AutomationTriggerRecord> Children(Automation? current, TenantAutomationChange change)
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
