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
