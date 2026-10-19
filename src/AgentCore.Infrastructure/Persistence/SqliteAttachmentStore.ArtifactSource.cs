using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAttachmentStore : ITenantAttachmentArtifactSourceStore
{
    public async ValueTask<ITenantWorkspaceArtifactSource> CaptureAttachmentArtifactSourceTenantAsync(TenantSessionRoot owner, Guid id, CancellationToken ct = default)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
            await RequireTenantOwner(db, owner, ct).ConfigureAwait(false);
        var item = await FindTenantAsync(owner.TenantId, owner.SessionId, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("attachment_source_changed");
        var observation = TenantAttachmentArtifactRules.Observation(owner, item);
        var files = TenantFiles(owner.TenantId, owner.SessionId);
        ValueTask<byte[]> Read(CancellationToken token) => files.ReadUnlinkedAsync("/working/" + id.ToString("N"), AttachmentLimits.MaxBytesEach, token);
        var bytes = await Read(ct).ConfigureAwait(false); TenantAttachmentRules.Content(item, bytes);
        if (await FindTenantAsync(owner.TenantId, owner.SessionId, id, ct).ConfigureAwait(false) != item) throw new SecurityConflictException("attachment_source_changed");
        return new AttachmentArtifactSource(item, observation, bytes, Read);
    }
}
