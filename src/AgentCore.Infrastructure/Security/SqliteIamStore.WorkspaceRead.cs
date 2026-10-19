using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireWorkspaceReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.WorkspaceRead!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantWorkspaceReadOperationRules.Require(value, run, agent, current, session);
        if (value.ResourceKey is not null) TenantWorkspaceReadOperationRules.RequireResourceRevision(value, run, agent, await SqliteAgentInstanceStore.ReadResourcesAsync(db, agent.Instance.InstanceId, ct).ConfigureAwait(false));
        await TenantWorkspaceReadOperationRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, agent.Instance.InstanceId, value, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
