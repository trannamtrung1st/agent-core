using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class AutomationMutations
{
    public static Automation Update(
        Automation current,
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

        if (current.Status != AutomationStatus.Active)
        {
            throw AgentCoreErrors.Validation("Only an active registration can be updated.");
        }

        string normalized;
        try
        {
            normalized = TriggerText.RequireInstructions(intent);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }

        var scheduleChanged = !current.Schedule.SemanticEquals(schedule);
        var changed = scheduleChanged
            || !string.Equals(current.Instructions, normalized, StringComparison.Ordinal)
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
                scheduleChanged ? current.TriggerRevision + 1 : current.TriggerRevision,
                updatedAt);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }
    }

    public static Automation SetModelOverride(
        Automation current,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status != AutomationStatus.Active)
        {
            throw AgentCoreErrors.Validation("Only an active registration can change its model.");
        }

        Automation updated;
        try
        {
            updated = current.WithModelOverride(catalogKey, reasoningEffort, current.Revision, updatedAt);
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }

        if (string.Equals(updated.ModelOverrideCatalogKey, current.ModelOverrideCatalogKey, StringComparison.Ordinal)
            && string.Equals(updated.ModelOverrideReasoningEffort, current.ModelOverrideReasoningEffort, StringComparison.Ordinal))
        {
            return current;
        }

        return current.WithModelOverride(catalogKey, reasoningEffort, current.Revision + 1, updatedAt);
    }

    public static Automation Cancel(
        Automation current,
        long expectedRevision,
        DateTimeOffset cancelledAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status == AutomationStatus.Cancelled)
        {
            return current;
        }

        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status is not AutomationStatus.Active and not AutomationStatus.SuspendedPolicy)
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
