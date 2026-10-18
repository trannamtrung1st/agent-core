using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantOperationReceipt?> TryCommitSandboxControlAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); var tenant = next.Tenant.TenantId;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } previous)
                return ValueTask.FromResult<TenantOperationReceipt?>(TenantLocalEffectRules.MatchingReceipt(previous, tenant, commit));
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(tenant, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var source = request.RunId != run.AgentRunId ? sessionBacking.ReadTenantRun(tenant, request.RunId) : null;
            TenantSandboxControlEffectRules.Validate(request, decision, _operationGrants.GetValueOrDefault(commit.RequestId), run, commit, source);
            var agent = FindTenantAgentAsync(tenant, request.Operation.InstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            TenantSessionRules.RequireExecutor(before, agent); ValidateAgentActor(before, request.ExecutorPrincipalId);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
            TenantApprovalRules.Validate(request, run, agent, requireCurrentWitness: false, source: source);
            (SandboxLease Lease, SandboxExecution? Execution)? target = null;
            if (commit.Outcome == TenantOperationOutcome.Completed)
            {
                if (request.ExpiresAt <= at) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "approval_expired" };
                else
                {
                    if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                        throw new SecurityConflictException("operation_witness_changed");
                    ValidateAgentActor(before, decision.HumanPrincipalId);
                    if (request.OriginalInitiatorPrincipalId is { } requester) ValidateAgentActor(before, requester);
                    target = RequireSandboxControlOperation(request, run, agent);
                    TenantSandboxControlOperationRules.RequirePermissionsAsync(before, sessionBacking.ReadTenantSession(tenant, request.SessionId)!.Owner,
                        request.Operation.SandboxControl!.ProfileId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
                }
            }
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            TenantSandboxControlEffectRules.Audit(next, expectedSecurityRevision, request, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, null);
            var complete = commit.Outcome == TenantOperationOutcome.Completed;
            var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
            var receipt = new TenantOperationReceipt(commit.OperationId, request.RequestId, tenant, run.AgentRunId, run.SessionId,
                run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, agent.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
                agent.Instance.Revision, agent.Instance.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
            SandboxLease? lease = null; SandboxExecution? execution = null;
            if (complete)
            {
                var current = target!.Value;
                lease = request.Operation.SandboxControl!.Kind == SandboxControlKind.ResetEnvironment ? TenantSandboxControlRules.Apply(current.Lease)
                    : current.Lease with { Revision = checked(current.Lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
                if (request.Operation.SandboxControl.Kind == SandboxControlKind.StopProcess)
                    execution = TenantSandboxControlRules.ApplyStop(current.Execution!, audit with { AuditId = request.RequestId, ActorPrincipalId = decision.HumanPrincipalId });
            }
            ct.ThrowIfCancellationRequested(); sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            if (lease is not null) sandboxBacking.SaveTenantLease(lease);
            if (execution is not null) sandboxBacking.SaveTenantExecution(execution);
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
