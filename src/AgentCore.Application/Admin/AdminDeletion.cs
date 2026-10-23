using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record AdminDeletionReferenceCounts(
    int Sessions,
    int LearnedMemoryItems,
    int Automations,
    int TriggerOccurrences,
    int AgentRuns,
    int Approvals,
    int Activations,
    int Instances)
{
    public bool HasReferences =>
        Sessions > 0
        || LearnedMemoryItems > 0
        || Automations > 0
        || TriggerOccurrences > 0
        || AgentRuns > 0
        || Approvals > 0
        || Activations > 0
        || Instances > 0;
}

public sealed record AdminDraftRevisionWitness(Guid DraftId, long Revision);

public sealed record AdminPublicationRevisionWitness(int Version, long MetadataRevision);

public sealed record AdminDefinitionDeleteWitness(
    IReadOnlyList<AdminDraftRevisionWitness> Drafts,
    IReadOnlyList<AdminPublicationRevisionWitness> Publications);
