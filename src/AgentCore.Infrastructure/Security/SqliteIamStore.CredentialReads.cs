using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireCredentialReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.CredentialRead!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var selected = new List<TenantCredentialBinding>();
        foreach (var r in TenantApprovalRules.CredentialReadReferences(value))
            selected.Add(await CredentialOperationTarget(db, request.TenantId, agent.Instance.InstanceId, r.BindingId, r.CredentialId, ct).ConfigureAwait(false));
        TenantCredentialReadOperationRules.Require(value, run, agent, session, await SandboxBaseline(db, agent, ct).ConfigureAwait(false), selected);
        await TenantCredentialReadOperationRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, value,
            agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
