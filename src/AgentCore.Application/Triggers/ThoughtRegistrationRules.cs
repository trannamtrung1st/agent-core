using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class ThoughtRegistrationRules
{
    public static ScheduleAdmission ManualAdmission(TriggerRegistration r, DateTimeOffset now) =>
        new(ScheduleAdmissionKind.Admit, TriggerScheduleCalculator.Truncate(now), r.NextOccurrenceAtUtc,
            0, null, null, r.Status, r.OccurrenceCount);
    public static void ValidateSave(TriggerRegistration? current, TriggerRegistration proposed, long expectedRevision)
    {
        if (proposed.Provenance.AuthorizationOrigin != TriggerAuthorizationOrigin.AdminThought
            || (current is not null && current.Provenance.AuthorizationOrigin != TriggerAuthorizationOrigin.AdminThought))
            throw AgentCoreErrors.Forbidden("Only owner-created thought registrations can be changed here.");
        if ((current?.Revision ?? 0) != expectedRevision || proposed.Revision != expectedRevision + 1)
            throw AgentCoreErrors.Conflict("Thought registration revision is stale.");
        if (current?.Status == TriggerRegistrationStatus.Cancelled)
            throw AgentCoreErrors.Validation("Deleted thought registration cannot be restored.");
        if (proposed.Schedule is not FixedIntervalSchedule { IntervalSeconds: >= ThoughtIntent.MinIntervalSeconds }
            || proposed.EventSourceId is not null || proposed.Provenance.SourceSessionId is not null)
            throw AgentCoreErrors.Validation("Thought registration requires an owner-controlled interval of at least 15 seconds.");
        ThoughtIntent.Require(proposed.Intent);
    }
}
