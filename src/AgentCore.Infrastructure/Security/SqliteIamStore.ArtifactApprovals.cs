using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<TenantArtifactPublicationArguments> RequireArtifactOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run,
        TenantAgent agent, CancellationToken ct, AgentRun? source = null)
    {
        await TenantExecutionLineage.RequireRequesterAsync(run, (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        var session = await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var args = TenantArtifactReviewRules.Require(request, run, agent, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), session, source);
        await TenantArtifactReviewRules.ExecutorAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, request, new IamAuthorizer(this), ct).ConfigureAwait(false);
        if (request.Operation.ArtifactPublication?.Source is { ResourceKey: { } key } saved) TenantResourceRevisionRules.Require(key, saved.ResourceRevision, run, agent,
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, agent.Instance.InstanceId, ct).ConfigureAwait(false));
        if (request.Operation.ArtifactPublication?.Source is { AttachmentId: { } attachmentId } attachmentSource)
        {
            var attachment = await SqliteAttachmentStore.Owned(db, run.Owner.TenantId!.Value, run.SessionId).SingleOrDefaultAsync(a => a.AttachmentId == attachmentId.ToString("D") && a.ContentDeletedAtUtc == null, ct).ConfigureAwait(false);
            TenantAttachmentArtifactRules.Require(attachmentSource, session.Owner, attachment is null ? null : SqliteAttachmentStore.Domain(attachment));
        }
        return args;
    }
}
