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
    }

    public static TriggerSourceKind Source(AutomationTrigger trigger) => trigger.Kind switch
    { AutomationTriggerKind.CoreEvent => TriggerSourceKind.CoreEvent, AutomationTriggerKind.Event => TriggerSourceKind.ApplicationEvent, _ => TriggerSourceKind.Schedule };

    public static bool IsManual(TriggerOccurrence occurrence) => occurrence.SourceKind == TriggerSourceKind.ManualInvocation;

    public static TriggerSourceKind AdmissionSource(TriggerOccurrence occurrence)
    {
        if (!IsManual(occurrence)) return occurrence.SourceKind;
        using var json = JsonDocument.Parse(occurrence.EvidenceJson);
        return json.RootElement.TryGetProperty("triggerKind", out var kind) ? kind.GetString() switch
        { "Event" => TriggerSourceKind.ApplicationEvent, "CoreEvent" => TriggerSourceKind.CoreEvent, _ => TriggerSourceKind.Schedule } : TriggerSourceKind.Schedule;
    }

    public static string Evidence(Automation automation, object? triggerContext = null) =>
        TriggerText.RequireEvidence(JsonSerializer.Serialize(new
        {
            automationId = automation.AutomationId,
            name = automation.Name,
            instructions = automation.Instructions,
            automationRevision = automation.Revision,
            triggerKind = automation.Trigger.Kind.ToString(),
            triggerSummary = Describe(automation.Trigger),
            triggerContext
        }));

    public static string Describe(AutomationTrigger trigger) => trigger switch
    {
        CoreEventTrigger e => $"Core Event · {e.CoreEventKey}",
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
