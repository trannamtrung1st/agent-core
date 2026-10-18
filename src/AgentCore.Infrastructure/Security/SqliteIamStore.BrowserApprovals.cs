using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireBrowserOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.BrowserCommand!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantBrowserOperationRules.Require(value, run, agent, await SandboxBaseline(db, agent, ct).ConfigureAwait(false), session);
        if (request.Snapshot != TenantBrowserOperationRules.Snapshot(agent, value)) throw new SecurityConflictException("browser_target_changed");
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, agent.Instance.InstanceId, value, request.ExecutorPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
