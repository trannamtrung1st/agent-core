using AgentCore.Domain.Conversation;
using AgentCore.Application.Security;
namespace AgentCore.Application.Ports;

// Private source evidence, not authority. Callers separately require current IAM, ownership and a live Run/consent.
public sealed record TenantWorkspaceArtifactObservation(TenantWorkspaceReference Owner, string Path, string ContentType,
    long ByteSize, string Sha256Hex, string ObservationHash, string? ResourceKey = null, long? ResourceRevision = null,
    Guid? AttachmentId = null, Guid? EntryId = null, string? AttachmentName = null);

public interface ITenantWorkspaceArtifactSource
{
    TenantWorkspaceArtifactObservation Observation { get; }
    byte[] CopyContent();
    // Storage-only: checks mutable files or immutable captured bytes; never reenter IAM/database owners inside publication transactions/locks.
    ValueTask RequireCurrentAsync(CancellationToken ct = default);
}

public interface ITenantWorkspaceArtifactSourceStore
{
    ValueTask<ITenantWorkspaceArtifactSource> CaptureResourceSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default) =>
        throw new AccessDeniedException("resource_source_unavailable");
    ValueTask<ITenantWorkspaceArtifactSource> CaptureArtifactSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default);
}

public interface ITenantAttachmentArtifactSourceStore
{
    ValueTask<ITenantWorkspaceArtifactSource> CaptureAttachmentArtifactSourceTenantAsync(TenantSessionRoot owner, Guid attachmentId, CancellationToken ct = default);
}
