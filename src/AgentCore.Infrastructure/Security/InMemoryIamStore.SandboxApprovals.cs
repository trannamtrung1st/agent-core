using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireSandboxOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.SandboxCommand!; var tenant = request.TenantId;
        var source = sandboxBacking.TenantProfile(tenant, value.ProfileId, value.ProfileRevision);
        var session = sessionBacking.ReadTenantSession(tenant, value.SessionId);
        if (source is null || session is null || sandboxBacking.TenantProfile(tenant, value.ProfileId)?.Enabled != true)
            throw new AccessDeniedException("resource_unavailable");
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion).GetAwaiter().GetResult();
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        var lease = sandboxBacking.TenantLease(tenant, value.SessionId);
        TenantSandboxOperationRules.Require(value, run, agent, session, baseline, source, lease, ApprovalNow);
        var pinned = TenantSandboxOperationRules.Profile(run, value.ProfileId);
        if (request.Snapshot != TenantSandboxOperationRules.Snapshot(agent, pinned, lease)) throw new SecurityConflictException("sandbox_operation_witness_changed");
        if (pinned.RegistryCredentialId is { } credential)
            TenantSandboxProfileRules.RequireCredential(pinned, credentials?.ReadTenantCredential(tenant, credential)?.Value, ApprovalNow);
        TenantSandboxRuntimeRules.RequirePermissionsAsync(_tenants[tenant], run, pinned, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
}
