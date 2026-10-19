using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAttachmentStore : ITenantAttachmentArtifactSourceStore
{
    public ValueTask<ITenantWorkspaceArtifactSource> CaptureAttachmentArtifactSourceTenantAsync(TenantSessionRoot owner, Guid id, CancellationToken ct = default)
    {
        lock (TenantGate)
        {
            ct.ThrowIfCancellationRequested(); RequireTenantOwner(owner);
            var item = RetainedTenant(owner.TenantId, owner.SessionId, id) ?? throw new AccessDeniedException("attachment_source_changed");
            var observation = TenantAttachmentArtifactRules.Observation(owner, item);
            ValueTask<byte[]> Read(CancellationToken token)
            {
                lock (TenantGate)
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(_blobs.TryGetValue(id, out var current) ? current.ToArray() : throw new AccessDeniedException("attachment_source_changed"));
                }
            }
            var bytes = Read(ct).Result; TenantAttachmentRules.Content(item, bytes);
            return ValueTask.FromResult<ITenantWorkspaceArtifactSource>(new AttachmentArtifactSource(item, observation, bytes, Read));
        }
    }
}
