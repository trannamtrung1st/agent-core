using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class AutomationRules
{
    public static void ValidateSave(Automation? current, Automation proposed, long expectedRevision)
    {
        if ((current?.Revision ?? 0) != expectedRevision || proposed.Revision != expectedRevision + 1)
            throw AgentCoreErrors.Conflict("Automation revision is stale.");
        if (current?.Status == AutomationStatus.Cancelled)
            throw AgentCoreErrors.Validation("Deleted Automation cannot be restored.");
        if (current is not null && (!current.Owner.Equals(proposed.Owner)
            || current.Provenance.AuthorizationOrigin != proposed.Provenance.AuthorizationOrigin
            || current.Provenance.SourceSessionId != proposed.Provenance.SourceSessionId
            || current.Provenance.SourceEventId != proposed.Provenance.SourceEventId
            || current.Provenance.CreatedAt != proposed.Provenance.CreatedAt
            || current.Provenance.PresetId != proposed.Provenance.PresetId || current.Provenance.PresetVersion != proposed.Provenance.PresetVersion))
            throw AgentCoreErrors.Forbidden("Automation provenance cannot change.");
        foreach (var child in proposed.Triggers)
        {
            var prior = current?.Triggers.SingleOrDefault(t => t.TriggerId == child.TriggerId);
            if (prior is null && child.Revision != 1 && current is not null || prior is not null && (prior.Source != child.Source
                || child.Revision != prior.Revision + (prior.Configuration.SemanticEquals(child.Configuration) && prior.Enabled == child.Enabled ? 0 : 1)))
                throw AgentCoreErrors.Conflict("Trigger identity or revision is stale; replace a source with a new trigger.");
        }
    }

    public static TriggerSourceKind Source(AutomationTrigger? trigger) => trigger?.Kind switch
    { AutomationTriggerKind.CoreEvent => TriggerSourceKind.CoreEvent, AutomationTriggerKind.Event => TriggerSourceKind.ApplicationEvent, _ => TriggerSourceKind.Schedule };

    public static bool IsManual(TriggerOccurrence occurrence) => occurrence.SourceKind == TriggerSourceKind.ManualInvocation;

    public static TriggerSourceKind AdmissionSource(TriggerOccurrence occurrence)
    {
        if (!IsManual(occurrence)) return occurrence.SourceKind;
        return TriggerSourceKind.ManualInvocation;
    }

    public static string Evidence(Automation automation, object? triggerContext = null, AutomationTriggerRecord? child = null) =>
        TriggerText.RequireEvidence(JsonSerializer.Serialize(new
        {
            automationId = automation.AutomationId,
            name = automation.Name,
            instructions = automation.Instructions,
            automationRevision = automation.Revision,
            triggerId = child?.TriggerId,
            source = child?.Source,
            triggerKind = child?.Configuration.Kind.ToString() ?? (automation.IsSchedule ? "Schedule" : "Events"),
            triggerSummary = child is null ? Describe(automation) : Describe(child.Configuration),
            triggerContext
        }));

    public static string Describe(Automation automation) => automation.IsSchedule ? Describe(automation.Trigger!)
        : string.Join(" OR ", automation.Triggers.Select(t => Describe(t.Configuration)));

    public static string Describe(AutomationTrigger trigger) => trigger switch
    {
        CoreEventTrigger e => $"Built-in · {e.CoreEventKey}",
        EventTrigger e => $"Event {e.EventId:D}",
        ScheduleTrigger { Schedule: OneShotSchedule s } => $"Once · {s.AtUtc:O}",
        ScheduleTrigger { Schedule: FixedIntervalSchedule s } => $"Every {s.IntervalSeconds} seconds",
        ScheduleTrigger { Schedule: DailySchedule s } => $"Every {s.IntervalDays} day(s) · {s.LocalTime:HH:mm} · {s.TimeZoneId}",
        ScheduleTrigger { Schedule: WeeklySchedule s } => $"Every {s.IntervalWeeks} week(s) · {string.Join(", ", s.Weekdays)} · {s.LocalTime:HH:mm} · {s.TimeZoneId}",
        _ => throw new ArgumentException("Automation trigger is unavailable.")
    };

    public static TriggerOccurrence ManualOccurrence(Automation automation, ExecutionModelPin pin, DateTimeOffset now)
    {
        now = TriggerScheduleCalculator.Truncate(now);
        var key = $"manual:{automation.AutomationId:D}:{automation.Revision}:{now.ToUnixTimeMilliseconds()}";
        return new(TriggerScheduleAdmission.OccurrenceId(key), key, automation.AutomationId, automation.Owner,
            TriggerSourceKind.ManualInvocation, null, now, now, Evidence(automation), null, automation.TriggerRevision,
            OccurrenceRoutingDisposition.Pending, null, 0, null, null, null, modelPin: pin, executionTarget: automation.ExecutionTarget, completionDelivery: automation.CompletionDelivery);
    }
}
