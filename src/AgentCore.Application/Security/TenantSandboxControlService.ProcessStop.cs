using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxProcessStopReceipt(Guid SessionId, Guid ExecutionId, Guid Generation, long LeaseRevision,
    long ExecutionRevision, long SessionRevision, long SecurityRevision, bool TerminationConfirmed = false);

public sealed partial class TenantSandboxControlService
{
    public async ValueTask<TenantSandboxProcessStopReceipt> RequestProcessStopAsync(Guid tenant, HumanSessionReference human,
        TenantSandboxProcessStop stop, long expectedSecurityRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, stop.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, null, human, authorizer, ct).ConfigureAwait(false);
        var saved = await storage.ReadTenantSandboxRuntimeAsync(tenant, stop.SessionId, stop.ExecutionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
        var execution = saved.Execution ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, authorizer, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        TenantSandboxControlRules.Require(stop, session, lease, execution);
        if (saved.Owner != session.Owner || saved.SessionRevision != session.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { Revision = checked(expectedSecurityRevision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "sandbox.process.stop.request", stop.ExecutionId.ToString("D"), "allowed", "human_stop", time.GetUtcNow());
        var requested = await storage.TryRequestTenantSandboxProcessStopAsync(next, expectedSecurityRevision, stop, human, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        return new(stop.SessionId, stop.ExecutionId, requested.Lease!.Generation, requested.Lease.Revision, requested.Execution!.Revision,
            requested.SessionRevision, next.Tenant.Revision);
    }
}

public static partial class TenantSandboxControlRules
{
    public static void Require(TenantSandboxProcessStop stop, TenantSession session, SandboxLease lease, SandboxExecution execution)
    {
        Require(new TenantSandboxReset(stop.SessionId, stop.ExpectedSessionRevision, stop.ExpectedGeneration, stop.ExpectedLeaseRevision, stop.ExecutionId), session, lease);
        if (stop.ExecutionId == Guid.Empty || stop.ExpectedExecutionRevision < 1) throw new ArgumentException("Use an exact current execution for Stop.");
        if (execution.SessionId != stop.SessionId || execution.Id != stop.ExecutionId || execution.Generation != lease.Generation || execution.TenantId != lease.TenantId
            || execution.AgentInstanceId != lease.AgentInstanceId || execution.ExecutorPrincipalId != lease.ExecutorPrincipalId) throw new AccessDeniedException("resource_unavailable");
        if (execution.Revision != stop.ExpectedExecutionRevision || lease.ActiveExecutionId != execution.Id || execution.TerminationConfirmed || execution.ProcessStop is not null
            || lease.State is not (SandboxLeaseState.Executing or SandboxLeaseState.Provisioning) || execution.State is not (SandboxExecutionState.Dispatching or SandboxExecutionState.Running))
            throw new SecurityConflictException("sandbox_stop_unavailable");
    }
    public static void Audit(Guid tenant, TenantSandboxProcessStop stop, HumanSessionReference human, AuthorizationAudit audit)
    {
        if (audit.AuditId == Guid.Empty || audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "sandbox.process.stop.request"
            || audit.Target != stop.ExecutionId.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "human_stop")
            throw new ArgumentException("Invalid exact human Sandbox Stop audit.");
    }
    public static SandboxExecution ApplyStop(SandboxExecution execution, AuthorizationAudit audit)
    {
        var next = execution with { Revision = checked(execution.Revision + 1), ProcessStop = new(audit.AuditId, audit.ActorPrincipalId, audit.At) };
        TenantSandboxRuntimeRules.ValidateExecution(next); return next;
    }
    public static SandboxExecution? StartStop(SandboxLease lease, SandboxExecution execution, Guid requestId, long expectedExecutionRevision, long expectedLeaseRevision, DateTimeOffset at)
    {
        if (execution.ProcessStop is not { DispatchStartedAt: null } stop || stop.RequestId != requestId || stop.RequestedAt > at
            || execution.Revision != expectedExecutionRevision || lease.Revision != expectedLeaseRevision || lease.Generation != execution.Generation
            || lease.ActiveExecutionId != execution.Id || lease.State != SandboxLeaseState.Executing || execution.State != SandboxExecutionState.Running
            || execution.TerminationConfirmed || !execution.Background || execution.Phase != "execution" || execution.ProviderCommandId is null) return null;
        var next = execution with { Revision = checked(execution.Revision + 1), ProcessStop = stop with { DispatchStartedAt = at } };
        TenantSandboxRuntimeRules.ValidateExecution(next); return next;
    }
}
