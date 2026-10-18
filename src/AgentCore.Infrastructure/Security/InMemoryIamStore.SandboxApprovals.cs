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
        var baseline = SandboxBaseline(agent);
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
        var baseline = SandboxBaseline(agent);
        TenantSandboxControlOperationRules.Require(value, run, agent, baseline, session, lease, execution);
        if (request.Snapshot?.SandboxControl is not { } snapshot || snapshot.ProfileName != lease.Profile.Name || snapshot.ImageReference != lease.Profile.ImageReference)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
        TenantSandboxControlOperationRules.RequirePermissionsAsync(_tenants[tenant], session.Owner, value.ProfileId, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return (lease, execution);
    }

    private AgentCore.Domain.Definitions.AgentDefinition SandboxBaseline(TenantAgent agent) =>
        (Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(agent.TenantId, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion).GetAwaiter().GetResult()) ?? throw new AccessDeniedException("definition_unavailable");

    private TenantSessionRoot RequireSandboxReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.SandboxRead!; var tenant = request.TenantId;
        var session = sessionBacking.ReadTenantSession(tenant, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = sandboxBacking.TenantLease(tenant, value.SessionId);
        var id = value.Kind == SandboxReadKind.Profiles ? null : value.Kind == SandboxReadKind.Process ? value.ExecutionId : lease?.ActiveExecutionId ?? lease?.LastExecutionId;
        var execution = id is { } executionId ? sandboxBacking.TenantExecution(tenant, value.SessionId, executionId) : null;
        var sources = TenantApprovalRules.ReadProfiles(value).Select(p => sandboxBacking.TenantProfile(tenant, p.Id, p.Revision) ?? throw new AccessDeniedException("resource_unavailable")).ToArray();
        TenantSandboxReadOperationRules.Require(value, run, agent, SandboxBaseline(agent), session, new(session.Owner, session.Snapshot.Revision, lease, execution), sources);
        var labels = TenantApprovalRules.ReadSnapshotLabels(request.Operation, request.Snapshot!.SandboxRead!);
        if (labels.Any(p => !sources.Any(s => s.Id == p.Id && s.Revision == p.Revision && s.Name == p.Name))) throw new SecurityConflictException("sandbox_read_target_changed");
        TenantSandboxReadOperationRules.RequirePermissionsAsync(_tenants[tenant], session.Owner, value, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return session.Owner;
    }

}
