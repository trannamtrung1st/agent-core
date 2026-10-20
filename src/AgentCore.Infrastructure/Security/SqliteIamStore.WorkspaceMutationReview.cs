using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireWorkspaceMutationOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct, AgentRun? source = null)
    {
        await TenantExecutionLineage.RequireRequesterAsync(run, (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        var session = await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantWorkspaceMutationOperationRules.Require(request, run, agent, current, session, source);
        await TenantWorkspaceMutationOperationRules.ExecutorAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, request, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
