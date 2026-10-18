using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireSandboxOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent, SandboxExecution? reserved = null)
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
        TenantSandboxControlRules.RequireCurrentSelection(lease, agent, sandboxBacking.TenantProfile(tenant, value.ProfileId));
        TenantSandboxOperationRules.Require(value, run, agent, session, baseline, source, lease, ApprovalNow, reserved);
        var pinned = TenantSandboxOperationRules.Profile(run, value.ProfileId);
        if (request.Snapshot != TenantSandboxOperationRules.Snapshot(agent, pinned, lease, reserved?.InitializesGeneration)) throw new SecurityConflictException("sandbox_operation_witness_changed");
        if (pinned.RegistryCredentialId is { } credential)
            TenantSandboxProfileRules.RequireCredential(pinned, credentials?.ReadTenantCredential(tenant, credential)?.Value, ApprovalNow);
        TenantSandboxRuntimeRules.RequirePermissionsAsync(_tenants[tenant], run, pinned, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
    private (SandboxLease Lease, SandboxExecution? Execution) RequireSandboxControlOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.SandboxControl!; var tenant = request.TenantId;
        var session = sessionBacking.ReadTenantSession(tenant, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = sandboxBacking.TenantLease(tenant, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var execution = value.ExecutionId is { } executionId ? sandboxBacking.TenantExecution(tenant, value.SessionId, executionId) : null;
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion).GetAwaiter().GetResult();
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantSandboxControlOperationRules.Require(value, run, agent, baseline, session, lease, execution);
        if (request.Snapshot?.SandboxControl is not { } snapshot || snapshot.ProfileName != lease.Profile.Name || snapshot.ImageReference != lease.Profile.ImageReference)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
        TenantSandboxControlOperationRules.RequirePermissionsAsync(_tenants[tenant], session.Owner, value.ProfileId, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return (lease, execution);
    }

}
