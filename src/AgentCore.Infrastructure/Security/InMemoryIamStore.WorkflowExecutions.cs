using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkflowExecutionStore
{
    private WorkflowEvidenceAuthority WorkflowEvidence() => new(this, this, this, this, this, this, this, new(this), clock);
    private readonly Dictionary<(Guid Tenant, Guid Id, string Node), WorkflowProcessDecision> workflowDecisions = [];
    private readonly Dictionary<(Guid Tenant, Guid Id), WorkflowExecution> workflowExecutions = [];
    private readonly Dictionary<(Guid Tenant, Guid Id, string Node), WorkflowStepOperation> workflowSteps = [];
    public ValueTask<WorkflowExecution?> FindWorkflowExecutionAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowExecutions.GetValueOrDefault((tenant, execution))); }
    public ValueTask<WorkflowExecution?> FindWorkflowStartRequestAsync(Guid tenant, string scope, string key, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowExecutions.Values.SingleOrDefault(e => e.TenantId == tenant && e.Source.RequestScope == scope && e.RequestKey == key)); }
    public ValueTask<WorkflowPage<WorkflowExecution>> ListWorkflowExecutionsAsync(Guid tenant, IReadOnlyList<Guid>? allowedWorkflows, int page, int size, CancellationToken ct = default, Guid? reader = null)
    {
        ct.ThrowIfCancellationRequested(); if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) { var all = workflowExecutions.Values.Where(e => e.TenantId == tenant && (allowedWorkflows is null || allowedWorkflows.Contains(e.WorkflowId)))
            .Where(e => reader is null || WorkflowReadRules.CanReadAsync(_tenants[tenant], reader.Value, e, new(this), this, this, ct).GetAwaiter().GetResult())
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.ExecutionId.ToString("D"), StringComparer.Ordinal).ToArray(); return ValueTask.FromResult(new WorkflowPage<WorkflowExecution>(all.Skip(page * size).Take(size).ToArray(), all.Length, page, size)); }
    }
    public ValueTask<WorkflowExecution> StartHumanWorkflowAsync(HumanWorkflowStart command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Execution;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireHumanSession(value.TenantId, command.Human);
            var state = _tenants.GetValueOrDefault(value.TenantId) ?? throw new AccessDeniedException("tenant_unavailable");
            var version = workflowVersions.GetValueOrDefault((value.TenantId, value.WorkflowId, value.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
            WorkflowExecutionRules.Validate(value, version);
            if (value.Source.Kind != WorkflowInvocationKind.HumanManual || value.Source.PrincipalId != command.Human.PrincipalId || value.Source.AccountId != command.Human.AccountId
                || value.Source.SourceId != value.ExecutionId || value.Revision != 1 || value.Status != WorkflowExecutionStatus.PendingAdmission)
                throw new AccessDeniedException("workflow_source_mismatch");
            WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
            var previous = workflowExecutions.Values.SingleOrDefault(e => e.TenantId == value.TenantId && e.Source.RequestScope == value.Source.RequestScope && e.RequestKey == value.RequestKey);
            if (previous is not null) { if (previous.Fingerprint != value.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return ValueTask.FromResult(previous); }
            if (workflowDefinitions.GetValueOrDefault((value.TenantId, value.WorkflowId)) is not { Retired: false }) throw new AccessDeniedException("workflow_version_unavailable");
            RequireWorkflowParticipants(state, value, version, ct);
            if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
            if (workflowExecutions.Values.Count(e => e.TenantId == value.TenantId && !WorkflowExecutionRules.Terminal(e.Status)) >= 32)
                throw new SecurityConflictException("workflow_capacity_exceeded");
            workflowExecutions.Add((value.TenantId, value.ExecutionId), value); BumpTaskSecurity(state);
            _audit.Add(new(command.AuditId, value.TenantId, command.Human.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "human_manual", TaskNow));
            return ValueTask.FromResult(value);
        }
    }
    public ValueTask<bool> WriteWorkflowExecutionAsync(WorkflowExecutionWrite command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Execution;
        lock (_gate)
        {
            var before = workflowExecutions.GetValueOrDefault((value.TenantId, value.ExecutionId)); if (before is null || before.Revision != command.ExpectedRevision) return ValueTask.FromResult(false);
            var version = workflowVersions[(value.TenantId, value.WorkflowId, value.Version)]; WorkflowExecutionAuthority.ExecutionChange(before, value, version);
            if (before.SourceClosure is null && value.SourceClosure is { } closure)
            {
                var reason = WorkflowSourceClosureRules.ReasonAsync(before, _tenants[value.TenantId], this, this, this, TaskNow, ct).GetAwaiter().GetResult();
                if (reason != closure.ReasonCode || closure.At > TaskNow) throw new AccessDeniedException("workflow_source_closure_not_qualified");
            }
            if (value.Status == WorkflowExecutionStatus.Completed)
            {
                WorkflowExecutionAuthority.RequireSourceAsync(_tenants[value.TenantId], before, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
                var steps = workflowSteps.Values.Where(s => s.TenantId == value.TenantId && s.ExecutionId == value.ExecutionId).ToArray();
                if (!steps.Any(s => s.Kind == WorkflowNodeKind.EndSuccess && s.Status == WorkflowOperationStatus.SignalConsumed && s.ProofAccepted)
                    || !WorkflowEvidence().FinalProofAsync(_tenants[value.TenantId], before, version, steps, ct).GetAwaiter().GetResult().CanSucceed)
                    throw new AccessDeniedException("workflow_completion_not_qualified");
            }
            workflowExecutions[(value.TenantId, value.ExecutionId)] = value;
            _audit.Add(new(command.AuditId, value.TenantId, value.Source.PrincipalId, "workflows.observe", value.ExecutionId.ToString("D"), "recorded", value.Status.ToString(), TaskNow));
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<IReadOnlyList<WorkflowStepOperation>> ReadWorkflowStepsAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<WorkflowStepOperation>>(workflowSteps.Values.Where(s => s.TenantId == tenant && s.ExecutionId == execution).OrderBy(s => s.NodeId, StringComparer.Ordinal).Take(40).ToArray()); }
    public ValueTask<IReadOnlyList<DelegatedTask>> ReadWorkflowTasksAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<DelegatedTask>>(delegatedTasks.Values.Where(t => t.Source.TenantId == tenant && t.Source.Workflow?.ExecutionId == execution).Take(40).ToArray()); }
    public ValueTask<WorkflowStepOperation> ReserveWorkflowStepAsync(WorkflowStepReservation command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Operation;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var execution = workflowExecutions.GetValueOrDefault((command.TenantId, command.ExecutionId)) ?? throw new AccessDeniedException("workflow_unavailable");
            var version = workflowVersions[(execution.TenantId, execution.WorkflowId, execution.Version)]; var state = _tenants[execution.TenantId];
            WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
            WorkflowExecutionRules.Operation(execution, value, version);
            if (workflowSteps.TryGetValue((command.TenantId, command.ExecutionId, value.NodeId), out var previous)) return ValueTask.FromResult(previous);
            if (execution.Revision != command.ExpectedExecutionRevision || value.Status != WorkflowOperationStatus.Reserved || value.Revision != 1
                || value.Outcome is not null || value.ProofAccepted || value.EvaluationAttemptId is not null || value.DecisionActorId is not null
                || workflowSteps.Values.Any(s => s.TenantId == command.TenantId && s.OperationId == value.OperationId)) throw new SecurityConflictException("workflow_step_conflict");
            workflowSteps.Add((command.TenantId, command.ExecutionId, value.NodeId), value); return ValueTask.FromResult(value);
        }
    }
    public ValueTask<bool> WriteWorkflowStepAsync(WorkflowStepWrite command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Operation;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var before = workflowSteps.GetValueOrDefault((value.TenantId, value.ExecutionId, value.NodeId)); if (before is null || before.Revision != command.ExpectedRevision) return ValueTask.FromResult(false);
            var execution = workflowExecutions[(value.TenantId, value.ExecutionId)]; var version = workflowVersions[(value.TenantId, execution.WorkflowId, execution.Version)];
            WorkflowExecutionAuthority.RequireSourceAsync(_tenants[value.TenantId], execution, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
            var task = before.TaskId is { } taskId ? delegatedTasks.GetValueOrDefault((value.TenantId, taskId)) : null;
            var worker = task?.InitialWorkerRunId is not null ? AgentCore.Application.Tasks.TaskSettlement.FinalRun(task, id => sessionBacking.ReadRetainedTenantOperationRun(value.TenantId, id)) : null;
            if (value.Kind == WorkflowNodeKind.AgentTask) WorkflowStepEvidenceRules.RequireWrite(execution, version, before, value, task, worker);
            else WorkflowEvidence().RequireStepWriteAsync(_tenants[value.TenantId], execution, version, before, value,
                workflowSteps.Values.Where(s => s.TenantId == value.TenantId && s.ExecutionId == value.ExecutionId).ToArray(), ct).GetAwaiter().GetResult();
            workflowSteps[(value.TenantId, value.ExecutionId, value.NodeId)] = value; return ValueTask.FromResult(true);
        }
    }
    public ValueTask<WorkflowProcessDecision?> FindWorkflowDecisionAsync(Guid tenant, Guid execution, string node, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(workflowDecisions.GetValueOrDefault((tenant, execution, node))); }
    public ValueTask<WorkflowProcessDecision> DecideWorkflowAsync(WorkflowDecisionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var fingerprint = WorkflowDecisionRules.Fingerprint(command);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireHumanSession(command.TenantId, command.Human);
            var execution = workflowExecutions.GetValueOrDefault((command.TenantId, command.ExecutionId)) ?? throw new AccessDeniedException("workflow_unavailable");
            var version = workflowVersions[(execution.TenantId, execution.WorkflowId, execution.Version)]; var state = _tenants[execution.TenantId];
            WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct, dispatch: false).GetAwaiter().GetResult();
            if (!WorkflowReadRules.CanReadAsync(state, command.Human.PrincipalId, execution, new(this), this, this, ct).GetAwaiter().GetResult()) throw new AccessDeniedException("resource_unavailable");
            AgentCore.Application.Tasks.TaskAuthority.RequireAsync(state, command.Human.PrincipalId, "workflows.decide", WorkflowCatalogAuthority.Scope(command.TenantId, execution.WorkflowId), new(this), ct).GetAwaiter().GetResult();
            var previous = workflowDecisions.Values.SingleOrDefault(r => r.TenantId == command.TenantId && r.ReviewerPrincipalId == command.Human.PrincipalId && r.RequestKey == command.RequestKey);
            if (previous is not null) { if (previous.Fingerprint != fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return ValueTask.FromResult(previous); }
            if (workflowDecisions.ContainsKey((command.TenantId, command.ExecutionId, command.NodeId))) throw new SecurityConflictException("workflow_decision_already_recorded");
            var step = workflowSteps.GetValueOrDefault((command.TenantId, command.ExecutionId, command.NodeId)) ?? throw new AccessDeniedException("workflow_decision_unavailable");
            var receipt = WorkflowDecisionRules.RequireAsync(command, state, execution, version, step, new(this), TaskNow, ct).GetAwaiter().GetResult();
            workflowDecisions.Add((command.TenantId, command.ExecutionId, command.NodeId), receipt);
            workflowSteps[(command.TenantId, command.ExecutionId, command.NodeId)] = WorkflowDecisionRules.Settle(step, receipt); BumpTaskSecurity(state);
            _audit.Add(new(command.AuditId, command.TenantId, command.Human.PrincipalId, "workflows.decide", command.ExecutionId.ToString("D"), "recorded", receipt.Approved ? "approve" : "reject", TaskNow));
            return ValueTask.FromResult(receipt);
        }
    }
    public ValueTask<WorkflowExecution> CancelWorkflowAsync(WorkflowCancelCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireHumanSession(command.TenantId, command.Human);
            var before = workflowExecutions.GetValueOrDefault((command.TenantId, command.ExecutionId)) ?? throw new AccessDeniedException("resource_unavailable");
            var state = _tenants[command.TenantId];
            if (!WorkflowReadRules.CanReadAsync(state, command.Human.PrincipalId, before, new(this), this, this, ct).GetAwaiter().GetResult()) throw new AccessDeniedException("resource_unavailable");
            var next = WorkflowCancellationRules.RequireAsync(command, state, before, new(this), TaskNow, ct).GetAwaiter().GetResult();
            if (next == before) return ValueTask.FromResult(before);
            WorkflowExecutionAuthority.ExecutionChange(before, next, workflowVersions[(before.TenantId, before.WorkflowId, before.Version)]);
            workflowExecutions[(command.TenantId, command.ExecutionId)] = next; BumpTaskSecurity(state);
            _audit.Add(new(command.AuditId, command.TenantId, command.Human.PrincipalId, "workflows.cancel", command.ExecutionId.ToString("D"), "allowed", "cancellation_requested", TaskNow));
            return ValueTask.FromResult(next);
        }
    }
    private int workflowRecoveryOffset;
    public ValueTask<IReadOnlyList<WorkflowExecution>> ListWorkflowRecoveryAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) {
            var bounded = Math.Clamp(limit, 1, 32); var all = workflowExecutions.Values.Where(e => !WorkflowExecutionRules.Terminal(e.Status)).OrderBy(e => e.CreatedAt).ThenBy(e => e.ExecutionId).ToArray();
            var rows = all.Skip(workflowRecoveryOffset).Take(bounded).ToArray(); workflowRecoveryOffset = rows.Length < bounded ? 0 : workflowRecoveryOffset + bounded;
            return ValueTask.FromResult<IReadOnlyList<WorkflowExecution>>(rows);
        }
    }
}
