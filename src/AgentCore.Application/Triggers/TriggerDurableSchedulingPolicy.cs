using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public enum UserSchedulingAdmissionDenialReason
{
    InstanceMissing,
    InstanceInactive,
    ProfileMissing,
    DefinitionUnavailable,
    SchedulingDisabled,
    UserSchedulingDisabled
}

public sealed record UserSchedulingAdmission(
    bool Allowed,
    AgentDefinition? Definition,
    UserSchedulingAdmissionDenialReason? DenialReason)
{
    public static UserSchedulingAdmission Allow(AgentDefinition definition) =>
        new(true, definition, null);

    public static UserSchedulingAdmission Deny(UserSchedulingAdmissionDenialReason reason) =>
        new(false, null, reason);
}

public static class TriggerDurableSchedulingPolicy
{
    public static async ValueTask<AgentDefinition?> ResolveEffectiveDefinitionAsync(
        TriggerOwner owner,
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.FindAsync(owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        return await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken).ConfigureAwait(false);
    }

    public static bool AllowsUserScheduling(AgentDefinition? definition) =>
        definition is not null
        && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule)
        && definition.TriggerPolicy is { Enabled: true, AllowUserScheduling: true };

    public static bool AllowsScheduledOccurrence(AgentDefinition? definition) =>
        definition is not null && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);

    public static async ValueTask<UserSchedulingAdmission> EvaluateUserSchedulingAsync(
        TriggerOwner owner,
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        IMemoryStore profiles,
        CancellationToken cancellationToken = default)
    {
        var occurrence = await EvaluateScheduledOccurrenceEligibilityAsync(
                owner,
                instances,
                definitions,
                profiles,
                cancellationToken)
            .ConfigureAwait(false);
        if (!occurrence.Allowed)
        {
            return UserSchedulingAdmission.Deny(occurrence.DenialReason!.Value);
        }

        if (!OccurrenceCompatibility.Allows(occurrence.Definition!, TriggerSourceKind.Schedule))
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.SchedulingDisabled);
        }

        if (!AllowsUserScheduling(occurrence.Definition))
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.UserSchedulingDisabled);
        }

        return UserSchedulingAdmission.Allow(occurrence.Definition!);
    }

    public static async ValueTask<UserSchedulingAdmission> EvaluateScheduledOccurrenceEligibilityAsync(
        TriggerOwner owner,
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        IMemoryStore profiles,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.FindAsync(owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.InstanceMissing);
        }

        if (instance.Lifecycle != AgentInstanceLifecycle.Active)
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.InstanceInactive);
        }

        var profile = await profiles.LoadProfileAsync(owner.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.ProfileMissing);
        }

        var definition = await ResolveEffectiveDefinitionAsync(owner, instances, definitions, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return UserSchedulingAdmission.Deny(UserSchedulingAdmissionDenialReason.DefinitionUnavailable);
        }

        return UserSchedulingAdmission.Allow(definition);
    }

    public static string PolicyMessage(UserSchedulingAdmissionDenialReason reason) => reason switch
    {
        UserSchedulingAdmissionDenialReason.InstanceMissing => "Agent instance is unavailable.",
        UserSchedulingAdmissionDenialReason.InstanceInactive => "Agent instance is not active.",
        UserSchedulingAdmissionDenialReason.ProfileMissing => "Owner profile is unavailable.",
        UserSchedulingAdmissionDenialReason.DefinitionUnavailable => "Scheduling is disabled for this agent.",
        UserSchedulingAdmissionDenialReason.SchedulingDisabled => "Scheduling is disabled for this agent.",
        UserSchedulingAdmissionDenialReason.UserSchedulingDisabled => "Scheduling is disabled for this agent.",
        _ => "Scheduling is disabled for this agent."
    };
}
