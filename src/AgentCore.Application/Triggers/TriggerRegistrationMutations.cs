using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class TriggerRegistrationMutations
{
    public static TriggerRegistration Update(
        TriggerRegistration current,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(schedule);
        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status != TriggerRegistrationStatus.Active)
        {
            throw AgentCoreErrors.Validation("Only an active registration can be updated.");
        }

        string normalized;
        try
        {
            normalized = TriggerText.RequireIntent(intent);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }

        var scheduleChanged = !current.Schedule.SemanticEquals(schedule);
        var changed = scheduleChanged
            || !string.Equals(current.Intent, normalized, StringComparison.Ordinal)
            || current.NextOccurrenceAtUtc != nextOccurrenceAtUtc
            || current.ExpiresAtUtc != expiresAtUtc;
        if (!changed)
        {
            return current;
        }

        try
        {
            return current.WithUpdate(
                normalized,
                schedule,
                nextOccurrenceAtUtc,
                expiresAtUtc,
                current.Revision + 1,
                scheduleChanged ? current.ScheduleRevision + 1 : current.ScheduleRevision,
                updatedAt);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }
    }

    public static TriggerRegistration Cancel(
        TriggerRegistration current,
        long expectedRevision,
        DateTimeOffset cancelledAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status == TriggerRegistrationStatus.Cancelled)
        {
            return current;
        }

        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status is not TriggerRegistrationStatus.Active and not TriggerRegistrationStatus.SuspendedPolicy)
        {
            throw AgentCoreErrors.Validation("Only an active or policy-suspended registration can be cancelled.");
        }

        try
        {
            return current.WithCancellation(current.Revision + 1, cancelledAt);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }
    }
}
