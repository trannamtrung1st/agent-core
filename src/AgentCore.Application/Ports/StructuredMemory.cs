using AgentCore.Domain.Memory;

namespace AgentCore.Application.Ports;

public static class MemoryLimits
{
    public const int MaxSubjectCharacters = 128;
    public const int MaxContentCharacters = 2000;
    public const int MaxSourceEntryIds = 8;
    public const int MaxSourceLabelCharacters = 64;
    public const int MaxActiveItems = 256;
    public const int SearchMaxItems = 20;
    public const int SearchMaxCharacters = 6000;
    public const int PromptMaxItems = 8;
    public const int PromptMaxCharacters = 6000;
}

public sealed record TrustedMemoryOwner(Guid SessionId);

public sealed record MemoryAdmissionContext(
    string Source,
    IReadOnlyList<string> ForbiddenFragments,
    IReadOnlySet<string> OccupiedTrustedSubjects);

public sealed record MemoryWriteProposal(
    MemoryKind Kind,
    string Subject,
    string Content,
    IReadOnlyList<Guid> SourceEntryIds);

public sealed record MemoryUpdateProposal(
    Guid MemoryId,
    string Subject,
    string Content,
    IReadOnlyList<Guid> SourceEntryIds);

public sealed record MemorySearchQuery(string? Text, MemoryKind? Kind);

public interface IStructuredMemoryStore
{
    ValueTask<StructuredMemoryItem?> FindAsync(
        Guid sessionId,
        Guid memoryId,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem?> FindActiveBySubjectAsync(
        Guid sessionId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask InsertAsync(StructuredMemoryItem item, CancellationToken cancellationToken = default);

    ValueTask SupersedeAsync(
        StructuredMemoryItem superseded,
        StructuredMemoryItem created,
        CancellationToken cancellationToken = default);

    ValueTask TombstoneAsync(StructuredMemoryItem tombstone, CancellationToken cancellationToken = default);

    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IStructuredMemoryService
{
    ValueTask<StructuredMemoryItem> WriteAsync(
        TrustedMemoryOwner owner,
        MemoryWriteProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem> UpdateAsync(
        TrustedMemoryOwner owner,
        MemoryUpdateProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem> DeleteAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem?> GetAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StructuredMemoryItem>> SearchAsync(
        TrustedMemoryOwner owner,
        MemorySearchQuery query,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default);
}
