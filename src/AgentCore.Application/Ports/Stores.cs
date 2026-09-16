using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentDefinitionStore
{
    ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<AgentDefinition?> GetAsync(
        string id,
        int? version = null,
        CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default);

    ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default);

    ValueTask<SessionCatalogPage> ListCatalogAsync(
        string? cursor,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Catalog listing is not implemented by this store.");
}

public sealed record SessionCatalogPage(
    IReadOnlyList<SessionSnapshot> Items,
    string? NextCursor,
    bool HasMore);

public interface IIdGenerator
{
    Guid NewId();
    Guid NewSessionId();
}

public sealed record EnvironmentEvent(
    Guid EventId,
    string Kind,
    IReadOnlyDictionary<string, string> Data);

public interface IEnvironmentEventIngress
{
    ValueTask PublishAsync(Guid sessionId, EnvironmentEvent input, CancellationToken cancellationToken = default);
}

public sealed record AttachmentRecord(
    Guid AttachmentId,
    Guid SessionId,
    string BlobKey,
    string DisplayName,
    string ContentType,
    long ByteSize,
    string Sha256Hex,
    AttachmentState State,
    bool Readable,
    Guid? EntryId,
    bool StageForNextTurn,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? BoundAt);

public interface IAttachmentStore
{
    ValueTask<AttachmentRecord> UploadPendingAsync(
        Guid sessionId,
        string displayName,
        string declaredContentType,
        Stream content,
        bool allowStoreUnread,
        CancellationToken cancellationToken = default);

    ValueTask AbortPendingAsync(Guid sessionId, Guid attachmentId, CancellationToken cancellationToken = default);

    ValueTask StageForNextTurnAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    ValueTask ValidateBindableAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AttachmentRecord>> BindToEntryAsync(
        Guid sessionId,
        Guid entryId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AttachmentRecord>> ListStagedPendingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AttachmentRecord>> ListForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<AttachmentRecord?> GetAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);

    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default);
}

public enum AttachmentProcessKind
{
    ExtractedText,
    Image,
    Unsupported
}

public sealed record AttachmentProcessResult(
    Guid AttachmentId,
    string ProcessorVersion,
    AttachmentProcessKind Kind,
    string DisplayName,
    string ContentType,
    string Text,
    string? Provenance,
    byte[]? StrippedImage,
    string? FailureCode);

public interface IAttachmentProcessor
{
    string Version { get; }

    ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);
}
