using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record AdminDeletionReferenceCounts(
    int Sessions,
    int LearnedMemoryItems,
    int TriggerRegistrations,
    int TriggerOccurrences,
    int WorkItems,
    int Approvals,
    int ConversationExecutions,
    int Instances)
{
    public bool HasReferences =>
        Sessions > 0
        || LearnedMemoryItems > 0
        || TriggerRegistrations > 0
        || TriggerOccurrences > 0
        || WorkItems > 0
        || Approvals > 0
        || ConversationExecutions > 0
        || Instances > 0;
}

public sealed record AdminDraftRevisionWitness(Guid DraftId, long Revision);

public sealed record AdminPublicationRevisionWitness(int Version, long MetadataRevision);

public sealed record AdminDefinitionDeleteWitness(
    IReadOnlyList<AdminDraftRevisionWitness> Drafts,
    IReadOnlyList<AdminPublicationRevisionWitness> Publications);

public sealed record AdminInstanceDeleteCommand(
    Guid InstanceId,
    long ExpectedRevision,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    AdminEventActorKind ActorKind = AdminEventActorKind.LocalOwner);

public sealed record AdminDefinitionDeleteCommand(
    string DefinitionId,
    AdminDefinitionDeleteWitness Witness,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    AdminEventActorKind ActorKind = AdminEventActorKind.LocalOwner);

public static class AdminDeletionMessages
{
    public const string DefinitionChanged =
        "This definition changed. Refresh and try deleting again.";

    public static string InstanceBlocked(AdminDeletionReferenceCounts counts)
    {
        var lines = new List<string>
        {
            "Cannot delete this instance.",
            "",
            "It is still referenced by:"
        };
        Append(lines, counts.Sessions, "session", "sessions");
        Append(lines, counts.LearnedMemoryItems, "learned memory item", "learned memory items");
        Append(lines, counts.TriggerRegistrations, "trigger registration", "trigger registrations");
        Append(lines, counts.TriggerOccurrences, "trigger occurrence", "trigger occurrences");
        Append(lines, counts.WorkItems, "background work item", "background work items");
        Append(lines, counts.Approvals, "approval", "approvals");
        Append(lines, counts.ConversationExecutions, "conversation execution", "conversation executions");
        lines.Add("");
        lines.Add("Archive keeps it inactive without breaking history.");
        return string.Join('\n', lines);
    }

    public static string DefinitionBlocked(AdminDeletionReferenceCounts counts)
    {
        var lines = new List<string>
        {
            "Cannot delete this definition.",
            "",
            "It is still referenced by:"
        };
        Append(lines, counts.Instances, "agent instance", "agent instances");
        Append(lines, counts.Sessions, "session", "sessions");
        Append(lines, counts.WorkItems, "background work item", "background work items");
        Append(lines, counts.Approvals, "approval", "approvals");
        Append(lines, counts.ConversationExecutions, "conversation execution", "conversation executions");
        lines.Add("");
        lines.Add("Deleting a definition does not remove instances, sessions, or their history.");
        return string.Join('\n', lines);
    }

    public static string DefinitionAlreadyExists(string definitionId) =>
        $"Definition '{definitionId}' already exists. Open it to create or edit a draft.";

    private static void Append(List<string> lines, int count, string singular, string plural)
    {
        if (count <= 0)
        {
            return;
        }

        lines.Add($"• {count} {(count == 1 ? singular : plural)}");
    }
}
