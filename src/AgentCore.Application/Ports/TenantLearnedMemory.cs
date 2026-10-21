using AgentCore.Application.Memory;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Ports;

public sealed record TenantMemorySource(Guid TenantId, Guid MemoryId, string ContentHash, bool MetadataOnly = false);
public sealed record TenantMemoryObservation(IReadOnlyList<TenantLearnedMemory> Items, IReadOnlyList<TenantMemorySource> Sources,
    long SecurityRevision, long InstanceRevision, Guid? UserPrincipalId, bool ReadAllowed);
public sealed record TenantMemoryChange(TenantLearnedMemory Value, long ExpectedRevision);
// Prepared by the owning store from the live frame. A plan is data for one atomic outcome,
// never a grant, model argument or independently executable write.
public sealed record TenantMemoryPlan(TenantRuntimeFrame Frame, Guid ResponseId, DateTimeOffset PreparedAt, Guid AuditId,
    IReadOnlyList<MemoryProposal> Proposals, IReadOnlyList<Guid> CandidateIds,
    IReadOnlyList<TenantMemoryChange> Changes, IReadOnlyList<MemoryReceipt> Receipts);
public interface ITenantLearnedMemoryStore
{
    ValueTask<TenantMemoryObservation> ReadTenantRunMemoryAsync(TenantRuntimeFrame frame, CancellationToken ct = default);
    ValueTask RequireTenantMemoryProjectionAsync(TenantRuntimeFrame frame, TenantMemoryObservation observation, CancellationToken ct = default);
    ValueTask RequireTenantMemorySourcesAsync(AgentCore.Application.Security.TenantRunReference run, IReadOnlyList<TenantMemorySource> sources,
        Guid? generation, CancellationToken ct = default);
    ValueTask<TenantMemoryPlan> PrepareTenantLearningAsync(TenantRuntimeFrame frame, SessionSnapshot proposed, IReadOnlyList<MemoryProposal> proposals,
        IReadOnlyList<Guid> candidateIds, Guid auditId, DateTimeOffset preparedAt, CancellationToken ct = default);
}
