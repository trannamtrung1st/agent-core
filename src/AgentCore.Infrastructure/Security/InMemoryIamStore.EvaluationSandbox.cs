using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private readonly Dictionary<(Guid Tenant, Guid Execution), EvaluationSandboxExecution> sandboxEvaluations = [];
    public ValueTask<EvaluationSandboxExecution?> FindSandboxEvaluationAsync(Guid tenant, Guid executionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(EvaluationPersistence.Copy(sandboxEvaluations.GetValueOrDefault((tenant, executionId)))); }
    public ValueTask<IReadOnlyList<EvaluationSandboxExecution>> ListSandboxEvaluationsAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<EvaluationSandboxExecution>>(EvaluationPersistence.Copy(sandboxEvaluations.Values
            .Where(v => v.Phase == EvaluationSandboxPhase.AwaitingReview
                ? (v.RetryCleanupAt ?? v.CreatedAt) <= now || _approvals.GetValueOrDefault(v.Approval.RequestId)?.Status != TenantApprovalStatus.Pending
                : v.Phase != EvaluationSandboxPhase.Settled ? v.DispatchExpiresAt <= now
                : (!v.FindingPublished || v.CleanupPending) && (v.RetryCleanupAt is null || v.RetryCleanupAt <= now))
            .OrderBy(v => v.RetryCleanupAt ?? v.DispatchExpiresAt ?? v.CreatedAt).Take(Math.Clamp(limit, 1, 32)).ToArray()));
    }
    public ValueTask<bool> TryReserveSandboxEvaluationAsync(TenantSecurityState next, long securityRevision, EvaluationSandboxExecution value,
        long attemptRevision, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default) => ValueTask.FromResult(WriteDefinition(next, securityRevision, audit, () => {
            if (sandboxEvaluations.Values.Any(e => e.TenantId == value.TenantId && e.AttemptId == value.AttemptId && e.CheckId == value.CheckId)) return null;
            var attempt = evaluationAttempts.GetValueOrDefault((value.TenantId, value.AttemptId));
            var source = sessionBacking.ReadTenantRun(value.TenantId, value.SubjectRunId);
            var script = Script(value.TenantId, value.Script.ScriptId, value.Script.Version);
            if (value.TenantId != next.Tenant.TenantId || value.ActorId != audit.ActorPrincipalId || attempt?.Revision != attemptRevision || source is null || script is null) return null;
            EvaluationSandboxRules.Reservation(value, attempt, source, script, ApprovalNow);
            RequireEvaluationApprovalCapacity(value.Approval);
            sandboxEvaluations.Add((value.TenantId, value.ExecutionId), EvaluationPersistence.Copy(value));
            _approvals.Add(value.Approval.RequestId, value.Approval); return value;
        }, ct, human) is not null);
    private void RequireEvaluationApprovalCapacity(TenantApprovalRequest request)
    {
        var recent = _approvals.Values.Where(r => r.TenantId == request.TenantId && (r.Status == TenantApprovalStatus.Pending || r.CreatedAt >= ApprovalNow - TenantApprovalLimits.Window)).ToArray();
        TenantApprovalLimits.Require(recent.Length, recent.Count(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId),
            recent.Count(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId), recent.Count(r => r.Fingerprint == request.Fingerprint),
            _approvals.Values.Count(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId), request.OriginalInitiatorPrincipalId is not null);
    }
    public ValueTask<bool> TrySaveSandboxEvaluationAsync(EvaluationSandboxExecution value, long expectedRevision, CancellationToken ct = default, long? expectedSecurityRevision = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!sandboxEvaluations.TryGetValue((value.TenantId, value.ExecutionId), out var before) || before.Revision != expectedRevision) return ValueTask.FromResult(false);
            EvaluationSandboxRules.Change(value, before, ApprovalNow);
            if (expectedSecurityRevision is not null && _tenants.GetValueOrDefault(value.TenantId)?.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            if (value.Outcome is EvaluationOutcome.Passed or EvaluationOutcome.Failed && expectedSecurityRevision is null && before.Phase != EvaluationSandboxPhase.Settled) return ValueTask.FromResult(false);
            if (before.Phase != value.Phase && value.Phase is EvaluationSandboxPhase.Reserved or EvaluationSandboxPhase.Dispatching
                || before.Phase != EvaluationSandboxPhase.Settled && value.Outcome is EvaluationOutcome.Passed or EvaluationOutcome.Failed)
            {
                if (expectedSecurityRevision is null) return ValueTask.FromResult(false);
                if (value.Human is { } human)
                {
                    try { RequireAgentHuman(value.TenantId, value.ActorId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId)); }
                    catch (AccessDeniedException) { return ValueTask.FromResult(false); }
                }
            }
            if (before.Phase == EvaluationSandboxPhase.AwaitingReview && value.Phase == EvaluationSandboxPhase.Reserved)
            {
                if (expectedSecurityRevision is null || _tenants.GetValueOrDefault(value.TenantId)?.Tenant.Revision != expectedSecurityRevision
                    || _approvals.GetValueOrDefault(value.Approval.RequestId) is not { Status: TenantApprovalStatus.Approved } approved
                    || approved.Operation != value.Approval.Operation || _approvalDecisions.GetValueOrDefault(approved.RequestId) is not { Status: TenantApprovalStatus.Approved }) return ValueTask.FromResult(false);
            }
            if (EvaluationSandboxRules.TechnicalAudit(before, value, modelIdSource.NewId(), ApprovalNow) is { } audit) _audit.Add(audit);
            sandboxEvaluations[(value.TenantId, value.ExecutionId)] = EvaluationPersistence.Copy(value); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryRecordSandboxSettlementUnavailableAsync(Guid tenant, Guid executionId, long expectedAttemptRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (sandboxEvaluations.GetValueOrDefault((tenant, executionId)) is not { Phase: EvaluationSandboxPhase.Settled } execution
                || evaluationAttempts.GetValueOrDefault((tenant, execution.AttemptId)) is not { Status: EvaluationStatus.Pending } attempt
                || attempt.Revision != expectedAttemptRevision
                || !attempt.Findings.Any(f => f.SandboxExecutionId == executionId && f.ReasonCode == "sandbox_approval_required")) return ValueTask.FromResult(false);
            var state = _tenants[tenant]; var revision = state.Tenant.Revision + 1;
            var next = attempt with { Revision = attempt.Revision + 1, CommitSequence = revision, UpdatedAt = ApprovalNow,
                Status = EvaluationStatus.Indeterminate, Outcome = EvaluationOutcome.Inconclusive, ReasonCode = "sandbox_evaluation_settlement_authority_changed",
                Findings = attempt.Findings.Select(f => f.SandboxExecutionId == executionId ? f with { Outcome = EvaluationOutcome.Inconclusive,
                    ReasonCode = "sandbox_evaluation_settlement_authority_changed", Summary = "Current source or execution authority was unavailable at settlement. No passing evaluation is recorded; read retained technical execution evidence separately." } : f).ToArray() };
            evaluationAttempts[(tenant, attempt.AttemptId)] = EvaluationPersistence.Copy(next);
            sandboxEvaluations[(tenant, executionId)] = execution with { FindingPublished = true, Revision = execution.Revision + 1 };
            _tenants[tenant] = state with { Tenant = state.Tenant with { Revision = revision } };
            _audit.Add(new(modelIdSource.NewId(), tenant, execution.ActorId, "evaluations.recover", attempt.AttemptId.ToString("D"), "recorded", next.ReasonCode, ApprovalNow));
            return ValueTask.FromResult(true);
        }
    }

}
