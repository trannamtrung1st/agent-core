using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Re-enabling permits future schedule slots; it never catches up work missed while disabled.
public static class AgentAvailabilityPolicy
{
    public static Automation SkipMissedSchedule(Automation value, DateTimeOffset at)
    {
        if (value.Status != AutomationStatus.Active || !value.IsSchedule || value.NextOccurrenceAtUtc is not { } due || due > at)
            return value;
        var expired = value.Schedule is OneShotSchedule || value.ExpiresAtUtc <= at;
        var next = expired ? null : TriggerScheduleCalculator.InitialNext(value.Schedule, at);
        return value.WithScheduleAdvance(expired ? AutomationStatus.Expired : next is null ? AutomationStatus.Completed : value.Status,
            next, value.OccurrenceCount, checked(value.Revision + 1), at, "Disabled interval skipped; no work was executed.");
    }
}
