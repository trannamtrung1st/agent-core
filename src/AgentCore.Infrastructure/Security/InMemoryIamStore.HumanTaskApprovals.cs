using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantHumanTaskApprovalStore
{
    private readonly Dictionary<(Guid Tenant, Guid Operation), HumanTaskApprovalOutcome> humanTaskOutcomes = [];
    private (TenantSession Source, TenantAgent Worker) RequireHumanTaskConsentOperation(TenantApprovalRequest request, CancellationToken ct)
    {
        TenantTaskConsentRules.RequireHumanShape(request); var task = request.Operation.TaskDelegation!.Task; var state = _tenants[request.TenantId];
        var source = sessionBacking.ReadTenantSession(request.TenantId, task.Source.SessionId) ?? throw new AccessDeniedException("task_source_unavailable");
        var agent = FindTenantAgentAsync(request.TenantId, task.Source.InstanceId, ct).Result ?? throw new AccessDeniedException("task_source_unavailable");
        TenantTaskConsentRules.RequireHumanSourceAsync(request, source, agent, state, new(this), TaskNow, ct).AsTask().GetAwaiter().GetResult();
        return (source, RequireTaskConsentWorker(task, state, ct));
    }
    private TenantAgent ValidateHumanTaskAdmission(TenantApprovalRequest request, TenantHumanTaskAdmission admission, CancellationToken ct)
    {
        TenantTaskConsentRules.MatchHumanAdmission(request, admission); var state = _tenants[request.TenantId]; var task = admission.Task;
        if (admission.ExpectedSecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var current = RequireHumanTaskConsentOperation(request, ct);
        if (delegatedTasks.ContainsKey((request.TenantId, task.TaskId)) || delegatedTasks.Values.Any(t => t.Source.TenantId == request.TenantId
            && t.Source.OperationScope == task.Source.OperationScope && t.Request.RequestKey == task.Request.RequestKey)) throw new SecurityConflictException("task_request_conflict");
        TaskAdmissionFactory.RequireHumanGraph(admission, current.Source, current.Worker, TaskNow);
        TaskAuthority.Bounds(task, delegatedTasks.Values.Where(t => t.Source.TenantId == request.TenantId).ToArray(), TaskNow, current.Worker.Instance.Cooperation!);
        var definition = admission.Run?.Admission.Configuration?.Definition ?? InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(current.Worker), current.Worker.Instance.SettingsOverrides);
        var budget = ExecutionBudgetResolver.Resolve(definition, current.Worker.Instance.ExecutionBudgets, false).Profile;
        if (TaskBudgetRules.Allocate(null, budget, [], _ => null, TaskNow, reportOnCompletion: task.Request.ReportOnCompletion) != task.Assignment.Budget)
            throw new SecurityConflictException("task_budget_changed");
        foreach (var id in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
            if (!delegatedTasks.TryGetValue((request.TenantId, id), out var dependency)
                || sessionBacking.ReadTenantSession(request.TenantId, dependency.Source.SessionId) is null
                || !TaskAuthority.CanReadHumanCommandAsync(state, task.Source.PrincipalId, dependency, new(this), ct).Result)
                throw new AccessDeniedException("task_dependency_unavailable");
        if (admission.Run is { } run) RequireSpaceExecutionSource(run, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
        return current.Worker;
    }
    public ValueTask<TenantApprovalRequest?> TryPrepareHumanTaskApprovalAsync(TaskAdmission preparation, TenantApprovalRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(request.TenantId, out var state) || state.Tenant.Revision != preparation.ExpectedSecurityRevision) return ValueTask.FromResult<TenantApprovalRequest?>(null);
            if (preparation.Actor.Human is not { } human || preparation.Actor.Agent is not null || human.PrincipalId != request.ExecutorPrincipalId
                || request.Status != TenantApprovalStatus.Pending || request.Revision != 1 || request.ExpiresAt <= TaskNow)
                throw new AccessDeniedException("task_consent_unavailable");
            RequireHumanSession(request.TenantId, human);
            _ = RequireTaskSource(request.TenantId, preparation.SourceSessionId, preparation.Actor, "tasks.delegate", ct);
            var duplicate = _approvals.Values.SingleOrDefault(r => r.TenantId == request.TenantId && r.Operation.TaskDelegation is { } p
                && p.Task.Source.OperationScope == preparation.Task.Source.OperationScope && p.Task.Request.RequestKey == preparation.Task.Request.RequestKey);
            if (duplicate is not null) { TenantApprovalRules.MatchTaskDelegationRequest(duplicate, preparation.Request); return ValueTask.FromResult<TenantApprovalRequest?>(duplicate); }
            var admission = new TenantHumanTaskAdmission(preparation.Task, preparation.WorkerSession, preparation.WorkerRun, preparation.ExpectedSecurityRevision, preparation.AuditId);
            _ = ValidateHumanTaskAdmission(request, admission, ct);
            if (!TenantTaskConsentRules.HumanNeedsReviewAsync(state, preparation.Task, new(this), ct).Result) throw new AccessDeniedException("task_review_not_required");
            var qualified = state.Users.Where(u => u.Status == IdentityStatus.Active).Any(u => {
                try { TenantTaskConsentRules.ReviewerAsync(state, request, u.PrincipalId, new(this), ct).AsTask().GetAwaiter().GetResult(); return true; }
                catch (AccessDeniedException) { return false; }
            });
            if (!qualified) throw new AccessDeniedException("no_eligible_approver");
            var recent = _approvals.Values.Where(r => r.TenantId == request.TenantId && (r.Status == TenantApprovalStatus.Pending || r.CreatedAt >= TaskNow - TenantApprovalLimits.Window)).ToArray();
            TenantApprovalLimits.Require(recent.Length, recent.Count(r => r.ExecutorPrincipalId == human.PrincipalId), recent.Count(r => r.OriginalInitiatorPrincipalId == human.PrincipalId),
                recent.Count(r => r.Fingerprint == request.Fingerprint), recent.Count(r => r.RootActivationId == request.RootActivationId), true);
            if (_approvals.ContainsKey(request.RequestId) || _approvals.Values.Any(r => r.Operation.OperationId == request.Operation.OperationId)) throw new SecurityConflictException("identity_conflict");
            _approvals.Add(request.RequestId, request); BumpTaskSecurity(state);
            _audit.Add(new(preparation.AuditId, request.TenantId, human.PrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", TaskNow));
            return ValueTask.FromResult<TenantApprovalRequest?>(request);
        }
    }
    public ValueTask<HumanTaskApprovalOutcome?> FindHumanTaskApprovalOutcomeAsync(Guid tenant, Guid operation, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(humanTaskOutcomes.GetValueOrDefault((tenant, operation))); }
    public ValueTask<bool> TrySettleHumanTaskApprovalAsync(ApprovalProcessedSignal signal, TenantHumanTaskAdmission? admission, string? refusalReason, long expectedSecurityRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_approvalSignals.TryGetValue(signal.SignalId, out var currentSignal) || currentSignal != signal || currentSignal.ProcessedAt is not null
                || !_approvals.TryGetValue(signal.RequestId, out var request) || request.TenantId != signal.TenantId || request.Revision != signal.DecisionRevision
                || request.RunId != signal.RunId || request.Status == TenantApprovalStatus.Pending) return ValueTask.FromResult(false);
            TenantTaskConsentRules.RequireHumanShape(request); var state = _tenants[request.TenantId];
            if (state.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            if (humanTaskOutcomes.ContainsKey((request.TenantId, request.Operation.OperationId))) return ValueTask.FromResult(false);
            var reason = request.Status == TenantApprovalStatus.Approved ? refusalReason : TenantApprovalRules.DuplicateReason(request);
            TenantAgent? worker = null;
            if (request.Status == TenantApprovalStatus.Approved && reason is null)
            {
                if (request.ExpiresAt <= TaskNow) reason = "approval_expired";
                else try {
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("task_consent_unavailable");
                    if (decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
                        throw new AccessDeniedException("task_consent_unavailable");
                    TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new(this), ct).AsTask().GetAwaiter().GetResult();
                    worker = ValidateHumanTaskAdmission(request, admission ?? throw new AccessDeniedException("task_admission_unavailable"), ct);
                }
                catch (AccessDeniedException error) { reason = error.ReasonCode; }
                catch (SecurityConflictException error) { reason = error.ReasonCode; }
                catch (AgentCore.Application.Sessions.AgentCoreException error) when (error.Code == "TaskCapacityExceeded") { reason = "task_capacity_exceeded"; }
            }
            var admitted = worker is not null && reason is null; var task = request.Operation.TaskDelegation!.Task;
            var outcome = new HumanTaskApprovalOutcome(request.TenantId, request.RequestId, request.Operation.OperationId, admitted,
                admitted ? "task_created" : reason ?? "task_admission_unavailable", TaskNow,
                admitted ? new(task.TaskId, 1, task.WorkerSessionId, task.InitialWorkerRunId) : null); outcome.Validate();
            if (admitted)
            {
                if (admission!.Run is { } run) StageTaskGraph(new(request.TenantId, run.SessionId, worker!.Instance.InstanceId, worker.PrincipalId, task.Source.PrincipalId, task.CreatedAt), admission.Session!, run);
                delegatedTasks.Add((request.TenantId, task.TaskId), task);
            }
            humanTaskOutcomes.Add((request.TenantId, outcome.OperationId), outcome);
            _approvalSignals[signal.SignalId] = signal with { Revision = checked(signal.Revision + 1), ProcessedAt = TaskNow }; BumpTaskSecurity(state);
            _audit.Add(new(signal.SignalId, request.TenantId, request.ExecutorPrincipalId, TenantTaskConsentRules.AuditAction, request.ResourceId,
                admitted ? "allowed" : "denied", outcome.ReasonCode, TaskNow));
            return ValueTask.FromResult(true);
        }
    }
}
