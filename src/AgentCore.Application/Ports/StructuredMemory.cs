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

public sealed record MemoryAdmissionContext(
    string Source,
    IReadOnlyList<string> ForbiddenFragments,
    IReadOnlySet<string> OccupiedTrustedSubjects);

public interface IStructuredMemoryStore
{
    ValueTask<StructuredMemoryItem> ConsolidateAsync(IReadOnlyList<StructuredMemoryItem> sources, StructuredMemoryItem result, CancellationToken ct = default);

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

    ValueTask<StructuredMemoryItem?> FindIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        Guid memoryId,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem?> FindActiveIdentityUserBySubjectAsync(
        Guid instanceId,
        Guid profileId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountActiveIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem?> FindUserAsync(
        Guid profileId,
        Guid memoryId,
        CancellationToken cancellationToken = default);

    ValueTask<StructuredMemoryItem?> FindActiveUserBySubjectAsync(
        Guid profileId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveUserAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountActiveUserAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask<int> ResetActiveSessionScopeAsync(
        Guid sessionId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<int> ResetActiveIdentityUserScopeAsync(
        Guid instanceId,
        Guid profileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<int> ResetActiveUserScopeAsync(
        Guid profileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);
}
