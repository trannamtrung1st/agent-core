using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSandboxRuntimeSnapshot?> ReadTenantSandboxRuntimeAsync(Guid tenant, Guid session, Guid? executionId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var saved = sessionBacking.ReadTenantSession(tenant, session);
            if (saved is null) return ValueTask.FromResult<TenantSandboxRuntimeSnapshot?>(null);
            var lease = sandboxBacking.TenantLease(tenant, session); var id = executionId ?? lease?.ActiveExecutionId ?? lease?.LastExecutionId;
            return ValueTask.FromResult<TenantSandboxRuntimeSnapshot?>(new(saved.Owner, saved.Snapshot.Revision, lease,
                id is { } exact ? sandboxBacking.TenantExecution(tenant, session, exact) : null));
        }
    }
    public ValueTask<SandboxExecution?> FindTenantSandboxOperationAsync(Guid tenant, Guid session, Guid run, string toolCallId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(sessionBacking.ReadTenantSession(tenant, session) is null ? null : sandboxBacking.TenantOperation(tenant, session, run, toolCallId));
    }
    private (TenantSession Session, AgentRun Run) RequireSandboxReservation(TenantSandboxRuntimeWrite write, SandboxProfile source,
        SandboxMountAuthority? mounted, DateTimeOffset at, CancellationToken ct)
    {
        var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var session = sessionBacking.ReadTenantSession(tenant, write.Runtime.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var run = sessionBacking.ReadTenantRun(tenant, write.Runtime.RunId) ?? throw new AccessDeniedException("run_unavailable");
        var agent = FindTenantAgentAsync(tenant, session.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        var state = _tenants[tenant]; TenantSessionRules.RequireExecutor(state, agent);
        TenantSandboxRuntimeRules.RequireReservation(write, session, run, agent, state.Tenant.Revision, at);
        var savedSource = sandboxBacking.TenantProfile(tenant, source.Id, source.Revision);
        if (savedSource is null || sandboxBacking.TenantProfile(tenant, source.Id)?.Enabled != true)
            throw new AccessDeniedException("resource_unavailable");
        TenantSandboxControlRules.RequireCurrentSelection(sandboxBacking.TenantLease(tenant, session.Owner.SessionId), agent, sandboxBacking.TenantProfile(tenant, source.Id));
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).GetAwaiter().GetResult();
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantSandboxRuntimeRules.RequireCurrentPolicy(run, agent, baseline, source, savedSource, mounted);
        if (source.RegistryCredentialId is { } credential)
            TenantSandboxProfileRules.RequireCredential(source, credentials?.ReadTenantCredential(tenant, credential)?.Value, at);
        TenantSandboxRuntimeRules.RequirePermissionsAsync(state, run, source, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        return (session, run);
    }
    public ValueTask<bool> TryReserveTenantSandboxLeaseAsync(TenantSandboxRuntimeWrite write, SandboxLease lease, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate)
        {
            var (_, run) = RequireSandboxReservation(write, lease.Profile, lease.MountAuthority, at, ct);
            var previous = sandboxBacking.TenantLease(write.Runtime.Owner.TenantId!.Value, lease.SessionId);
            if ((previous?.Revision ?? 0) != expectedRevision || previous is null && sandboxBacking.HasLease(lease.SessionId)) return ValueTask.FromResult(false);
            if (sandboxBacking.TenantOperation(write.Runtime.Owner.TenantId.Value, lease.SessionId, write.Runtime.RunId, lease.AdmissionToolCallId ?? "") is not null)
                return ValueTask.FromResult(false);
            TenantSandboxRuntimeRules.ValidateNewLease(write, lease, expectedRevision, run, previous, at);
            sandboxBacking.SaveTenantLease(lease); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryReserveTenantSandboxExecutionAsync(TenantSandboxRuntimeWrite write, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default)
    {
        if (execution.ApprovalRequestId is not null || execution.OperationId is not null || execution.InitializesGeneration) throw new AccessDeniedException("sandbox_approval_context_required");
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate)
        {
            var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var lease = sandboxBacking.TenantLease(tenant, execution.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var (_, run) = RequireSandboxReservation(write, lease.Profile, lease.MountAuthority, at, ct);
            var old = sandboxBacking.TenantOperation(tenant, execution.SessionId, execution.AgentRunId, execution.ToolCallId);
            if (old is not null)
            {
                if (old.ActionHash != execution.ActionHash) throw new SecurityConflictException("operation_conflict");
                return ValueTask.FromResult(false);
            }
            if (sandboxBacking.HasExecution(execution.Id) || sandboxBacking.HasOperation(execution.SessionId, execution.AgentRunId, execution.ToolCallId)) return ValueTask.FromResult(false);
            TenantSandboxRuntimeRules.ValidateNewExecution(write, execution, lease, run, at);
            sandboxBacking.SaveTenantExecution(execution);
            sandboxBacking.SaveTenantLease(lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Executing,
                ActiveExecutionId = execution.Id, LastExecutionId = execution.Id, AdmissionRunId = execution.AgentRunId, AdmissionToolCallId = execution.ToolCallId, LastActivityAt = at });
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TrySaveTenantSandboxLeaseFactsAsync(Guid tenant, SandboxLease lease, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var root = sessionBacking.ResolveTenantSessionOwner(lease.SessionId); var old = sandboxBacking.TenantLease(tenant, lease.SessionId);
            if (root?.TenantId != tenant || root.ExecutorPrincipalId != lease.ExecutorPrincipalId || old?.Revision != expectedRevision) return ValueTask.FromResult(false);
            TenantSandboxRuntimeRules.ValidateLeaseFacts(old, lease);
            if (lease.ActiveExecutionId is { } active && sandboxBacking.TenantExecution(tenant, lease.SessionId, active)?.Generation != lease.Generation) throw new ArgumentException("Execution does not belong to this generation.");
            if (old.ActiveExecutionId is { } previous && lease.ActiveExecutionId != previous && lease.State != SandboxLeaseState.Terminated
                && sandboxBacking.TenantExecution(tenant, lease.SessionId, previous)?.TerminationConfirmed != true) throw new SecurityConflictException("sandbox_recovery_required");
            sandboxBacking.SaveTenantLease(lease); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TrySaveTenantSandboxExecutionFactsAsync(Guid tenant, SandboxExecution execution, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var root = sessionBacking.ResolveTenantSessionOwner(execution.SessionId); var old = sandboxBacking.TenantExecution(tenant, execution.SessionId, execution.Id);
            if (root?.TenantId != tenant || root.ExecutorPrincipalId != execution.ExecutorPrincipalId || old?.Revision != expectedRevision) return ValueTask.FromResult(false);
            TenantSandboxRuntimeRules.ValidateExecutionFacts(old, execution); sandboxBacking.SaveTenantExecution(execution); return ValueTask.FromResult(true);
        }
    }
}
