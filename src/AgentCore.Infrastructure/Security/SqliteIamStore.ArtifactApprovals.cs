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
        var session = await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var args = TenantArtifactReviewRules.Require(request, run, agent, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), session, source);
        await TenantArtifactReviewRules.ExecutorAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, request, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return args;
    }
}
