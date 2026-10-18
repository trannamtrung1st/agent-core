using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireBrowserOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.BrowserCommand!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var baseline = await SandboxBaseline(db, agent, ct).ConfigureAwait(false);
        TenantBrowserOperationRules.Require(value, run, agent, baseline, session);
        if (value.Credential is { } credential)
        {
            await TenantBrowserCredentialRules.RequireChatLineageAsync(run, async (id, token) => {
                var row = await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), token).ConfigureAwait(false);
                return row is null ? null : AgentRunStoreMapping.ToDomain(row);
            }, ct).ConfigureAwait(false);
            var bound = await CredentialOperationTarget(db, request.TenantId, request.Operation.InstanceId, credential.BindingId, credential.CredentialId, ct).ConfigureAwait(false);
            TenantBrowserCredentialRules.RequireBinding(run, agent, bound, baseline); TenantBrowserCredentialRules.RequireWitness(credential, bound);
        }
        if (request.Snapshot != TenantBrowserOperationRules.Snapshot(agent, value)) throw new SecurityConflictException("browser_target_changed");
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, agent.Instance.InstanceId, value, request.ExecutorPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
