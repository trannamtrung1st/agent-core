using System.Text.Json;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

public static class AdminEventFactory
{
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
}
