using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantAttachmentPage(IReadOnlyList<AttachmentRecord> Items, Guid? NextCursor, bool HasMore);
public sealed record TenantAttachmentUpload(AttachmentRecord Record, byte[] Bytes);
public sealed record TenantAttachmentMutation(TenantSessionRoot Owner, HumanSessionReference Human, long SecurityRevision,
    long SessionRevision, long InstanceRevision, Guid RequestId, AuthorizationAudit Audit);
public sealed record TenantAttachmentCleanupTarget(Guid TenantId, Guid SessionId, Guid AttachmentId, string Sha256Hex);
public sealed record TenantAttachmentCleanupPage(IReadOnlyList<TenantAttachmentCleanupTarget> Items, string? NextCursor);

// Actual storage ownership and verified bytes only; public mutations use the canonical IAM transaction owner below.
public interface ITenantAttachmentStore
{
    ValueTask<TenantAttachmentUpload> PrepareUploadTenantAsync(TenantSessionRoot owner, Guid attachmentId, Guid requestId, Guid actor,
        string name, string declaredType, Stream content, CancellationToken ct = default);
    ValueTask<bool> OwnsTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default);
    ValueTask<AttachmentRecord?> FindTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default);
    ValueTask<TenantAttachmentPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, CancellationToken ct = default, bool boundOnly = false);
    ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default);
}
public interface ITenantAttachmentMutationStore
{
    ValueTask<AttachmentRecord?> TryUploadAttachmentAsync(TenantAttachmentMutation mutation, TenantAttachmentUpload upload, CancellationToken ct = default);
    ValueTask<ArtifactRecord?> TryPublishMaterializationAsync(TenantAttachmentMutation mutation, AttachmentRecord source, ArtifactRecord artifact, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    ValueTask<bool> TryAbortAttachmentAsync(TenantAttachmentMutation mutation, Guid attachmentId, CancellationToken ct = default);
}
// Technical cleanup is derived only from retained expiry/deletion; no human/model supplied path or deletion authority.
public interface ITenantAttachmentCleanupStore
{
    ValueTask<TenantAttachmentCleanupPage> ListAttachmentCleanupAsync(string? after, int limit, CancellationToken ct = default);
    ValueTask<bool> CleanupAttachmentContentAsync(TenantAttachmentCleanupTarget target, CancellationToken ct = default);
}

// Source provenance only. Every use resolves current ownership, permissions and policy again.
public sealed record TenantAttachmentContextSource(Guid AttachmentId, Guid EntryId, string DisplayName, string ContentType,
    long ByteSize, string Sha256Hex, string? ProcessorVersion = null);
public sealed record TenantAttachmentContext(IReadOnlyList<AgentCore.Domain.Conversation.ConversationEntry> History,
    IReadOnlyList<SessionAttachmentManifestItem> Manifest, IReadOnlyList<AttachmentProcessResult> Contents,
    IReadOnlyList<TenantAttachmentContextSource> Sources);
