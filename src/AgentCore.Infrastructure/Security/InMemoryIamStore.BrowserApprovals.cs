using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireBrowserOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.BrowserCommand!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        TenantBrowserOperationRules.Require(value, run, agent, SandboxBaseline(agent), session);
        if (value.Credential is { } credential)
        {
            TenantBrowserCredentialRules.RequireChatLineageAsync(run, (id, _) => ValueTask.FromResult(sessionBacking.ReadTenantRun(request.TenantId, id)), CancellationToken.None).GetAwaiter().GetResult();
            var bound = ReadTenantCredentialBindingsAsync(request.TenantId, request.Operation.InstanceId).Result?.Bindings.SingleOrDefault(b => b.Binding.BindingId == credential.BindingId)
                ?? throw new AccessDeniedException("credential_binding_changed");
            TenantBrowserCredentialRules.RequireBinding(run, agent, bound, SandboxBaseline(agent)); TenantBrowserCredentialRules.RequireWitness(credential, bound);
        }
        if (request.Snapshot != TenantBrowserOperationRules.Snapshot(agent, value)) throw new SecurityConflictException("browser_target_changed");
        TenantBrowserOperationRules.RequirePermissionsAsync(_tenants[request.TenantId], agent.Instance.InstanceId, value, request.ExecutorPrincipalId,
            new IamAuthorizer(this), CancellationToken.None, request.Kind == ApprovalKind.ExceptionalGrant).GetAwaiter().GetResult();
    }
}
