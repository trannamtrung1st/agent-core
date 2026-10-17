using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> TryReserveApprovedSandboxExecutionAsync(TenantSandboxApprovalReservation review, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate)
        {
            var write = review.Write; var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var lease = sandboxBacking.TenantLease(tenant, execution.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var (_, run) = RequireSandboxReservation(write, lease.Profile, lease.MountAuthority, at, ct);
            var request = _approvals.GetValueOrDefault(review.RequestId);
            var decision = _approvalDecisions.GetValueOrDefault(review.RequestId);
            if (request?.TenantId != tenant || decision is null) throw new AccessDeniedException("approval_unavailable");
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadTenantRun(tenant, request.RunId);
            TenantSandboxApprovalRules.RequireApproved(review, request, decision, run, source, execution, at);
            if (sandboxBacking.ApprovalOperation(execution.OperationId!.Value) is { } previous)
            {
                if (previous.ApprovalRequestId != request.RequestId || previous.ActionHash != execution.ActionHash || previous.SessionId != execution.SessionId)
                    throw new SecurityConflictException("sandbox_approval_mismatch");
                return ValueTask.FromResult(false);
            }
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result!;
            TenantApprovalRules.Validate(request, run, agent, source: source);
            RequireSandboxOperation(request, run, agent);
            var state = _tenants[tenant];
            TenantSandboxApprovalRules.RequireHumanAsync(state, request, decision, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantSandboxRuntimeRules.ValidateNewExecution(write, execution, lease, run, at);
            if (sandboxBacking.HasExecution(execution.Id) || sandboxBacking.HasOperation(execution.SessionId, execution.AgentRunId, execution.ToolCallId)) return ValueTask.FromResult(false);
            var started = run.BeginApprovedExternalEffect(run.Revision, write.Runtime.Generation, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, at);
            var audit = TenantOperationAuditRules.WithEffectResult(review.Audit, state, run, request, null);
            audit = audit with { Execution = audit.Execution! with { Purpose = AgentCore.Domain.Security.ExecutionAuthorizationPurpose.EffectEligibility } };
            sessionBacking.SaveTenantOperationRun(started, run.Revision);
            sandboxBacking.SaveTenantExecution(execution);
            sandboxBacking.SaveTenantLease(lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Executing,
                ActiveExecutionId = execution.Id, LastExecutionId = execution.Id, AdmissionRunId = execution.AgentRunId,
                AdmissionToolCallId = execution.ToolCallId, LastActivityAt = at });
            _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
