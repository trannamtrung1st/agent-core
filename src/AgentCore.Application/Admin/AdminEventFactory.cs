using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AdminEventFactory
{
    public static AdminEventAppend DraftCreated(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid draftId,
        DefinitionDraftSourceKind sourceKind,
        int? sourceVersion,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.DraftCreated,
            "definition.draft",
            draftId.ToString("D"),
            1,
            null,
            JsonSerializer.Serialize(new
            {
                definitionId,
                draftId = draftId.ToString("D"),
                sourceKind = sourceKind.ToString(),
                sourceVersion
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend DraftDeleted(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid draftId,
        long revision,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.DraftDeleted,
            "definition.draft",
            draftId.ToString("D"),
            revision,
            null,
            JsonSerializer.Serialize(new
            {
                definitionId,
                draftId = draftId.ToString("D"),
                revision
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend ManagedInstanceCreated(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid instanceId,
        int version,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.ManagedInstanceCreated,
            "agent.instance",
            instanceId.ToString("D"),
            1,
            version,
            JsonSerializer.Serialize(new
            {
                definitionId,
                instanceId = instanceId.ToString("D"),
                version
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend InstanceDefinitionVersionChanged(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid instanceId,
        int fromVersion,
        int toVersion,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.InstanceDefinitionVersionChanged,
            "agent.instance",
            instanceId.ToString("D"),
            null,
            toVersion,
            JsonSerializer.Serialize(new
            {
                definitionId,
                instanceId = instanceId.ToString("D"),
                fromVersion,
                toVersion
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend PersonaChanged(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid instanceId,
        int activeVersion,
        long fromPersonaRevision,
        long toPersonaRevision,
        AgentIdentity persona,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.PersonaChanged,
            "agent.instance",
            instanceId.ToString("D"),
            toPersonaRevision,
            activeVersion,
            JsonSerializer.Serialize(new
            {
                definitionId,
                instanceId = instanceId.ToString("D"),
                fromPersonaRevision,
                personaRevision = toPersonaRevision,
                personaFingerprint = AdminPersonaHistoryFingerprint.Compute(persona)
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend InstanceLifecycleChanged(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        Guid instanceId,
        int activeVersion,
        AgentInstanceLifecycle fromLifecycle,
        AgentInstanceLifecycle toLifecycle,
        long instanceRevision,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var operation = (fromLifecycle, toLifecycle) switch
        {
            (AgentInstanceLifecycle.Active, AgentInstanceLifecycle.Archived) => AdminEventOperationKind.InstanceArchived,
            (AgentInstanceLifecycle.Archived, AgentInstanceLifecycle.Active) => AdminEventOperationKind.InstanceUnarchived,
            _ => throw AgentCoreErrors.Validation("Unsupported managed instance lifecycle transition.")
        };
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            operation,
            "agent.instance",
            instanceId.ToString("D"),
            instanceRevision,
            activeVersion,
            JsonSerializer.Serialize(new
            {
                definitionId,
                instanceId = instanceId.ToString("D"),
                fromLifecycle = fromLifecycle.ToString(),
                toLifecycle = toLifecycle.ToString()
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend PublicationCreated(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        int version,
        Guid draftId,
        long draftRevision,
        IReadOnlyList<string> changedSectionIds,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var sections = changedSectionIds.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in sections)
        {
            if (!AdminEventSummaryPolicy.PublicationChangedSectionIds.Contains(id))
            {
                throw AgentCoreErrors.Validation("Publication changedSections contains an unknown section id.");
            }
        }

        Array.Sort(sections, StringComparer.Ordinal);
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.PublicationCreated,
            "definition.publication",
            $"{definitionId}:{version}",
            draftRevision,
            version,
            JsonSerializer.Serialize(new
            {
                definitionId,
                version,
                draftId = draftId.ToString("D"),
                changedSections = sections
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend MemoryItemDeleted(
        Guid operationId,
        DateTimeOffset occurredAt,
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.MemoryItemDeleted,
            "learned-memory.item",
            memoryId.ToString("D"),
            null,
            null,
            JsonSerializer.Serialize(new
            {
                instanceId = instanceId.ToString("D"),
                memoryId = memoryId.ToString("D"),
                scope = scope.ToString(),
                sessionId = sessionId?.ToString("D")
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend MemoryScopeReset(
        Guid operationId,
        DateTimeOffset occurredAt,
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        int itemsRemoved,
        Guid? sessionId,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.MemoryScopeReset,
            "agent.instance",
            instanceId.ToString("D"),
            null,
            null,
            JsonSerializer.Serialize(new
            {
                instanceId = instanceId.ToString("D"),
                scope = scope.ToString(),
                itemsRemoved,
                sessionId = sessionId?.ToString("D")
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend TriggerRegistrationRevoked(
        Guid operationId,
        DateTimeOffset occurredAt,
        Guid instanceId,
        Guid registrationId,
        long revision,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.TriggerRegistrationRevoked,
            "trigger.registration",
            registrationId.ToString("D"),
            revision,
            null,
            JsonSerializer.Serialize(new
            {
                instanceId = instanceId.ToString("D"),
                registrationId = registrationId.ToString("D"),
                revision
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }

    public static AdminEventAppend PublicationDeprecated(
        Guid operationId,
        DateTimeOffset occurredAt,
        string definitionId,
        int version,
        long metadataRevision,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var append = new AdminEventAppend(
            operationId,
            occurredAt,
            actorKind,
            AdminEventOperationKind.PublicationDeprecated,
            "definition.publication",
            $"{definitionId}:{version}",
            metadataRevision,
            version,
            JsonSerializer.Serialize(new
            {
                definitionId,
                version,
                metadataRevision
            }));
        AdminEventSummaryPolicy.ValidateAppend(append);
        return append;
    }
}
