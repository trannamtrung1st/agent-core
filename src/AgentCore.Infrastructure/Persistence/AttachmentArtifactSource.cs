using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Persistence;

// Native-byte verification only. Metadata/ownership checks belong to the surrounding IAM transaction.
internal sealed class AttachmentArtifactSource(AttachmentRecord item, TenantWorkspaceArtifactObservation observation,
    byte[] bytes, Func<CancellationToken, ValueTask<byte[]>> current) : ITenantWorkspaceArtifactSource
{
    public TenantWorkspaceArtifactObservation Observation => observation;
    public byte[] CopyContent() => bytes.ToArray();
    public async ValueTask RequireCurrentAsync(CancellationToken ct = default)
    {
        try { TenantAttachmentRules.Content(item, await current(ct).ConfigureAwait(false)); ct.ThrowIfCancellationRequested(); }
        catch (Exception e) when (e is AgentCoreException or AccessDeniedException or IOException or UnauthorizedAccessException)
        { throw new SecurityConflictException("attachment_source_changed"); }
    }
}
