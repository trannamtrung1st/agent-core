using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkflowTaskStore
{
    private bool TaskSourceAvailable(DelegatedTask task, CancellationToken ct, bool dispatch = true)
    {
        if (task.Source.Workflow is not null)
        {
            try { RequireWorkflowTaskSource(task, _tenants.GetValueOrDefault(task.Source.TenantId) ?? throw new AccessDeniedException("tenant_unavailable"), ct, dispatch); return true; }
            catch (AccessDeniedException) { return false; }
        }
        var source = sessionBacking.ReadTenantSession(task.Source.TenantId, task.Source.RequireSessionId());
        return source is not null && !AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow);
    }
    private void RequireWorkflowTaskSource(DelegatedTask task, AgentCore.Domain.Security.TenantSecurityState state, CancellationToken ct, bool dispatch = true)
    {
        var witness = task.Source.Workflow ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        var execution = workflowExecutions.GetValueOrDefault((task.Source.TenantId, witness.ExecutionId)) ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        var version = workflowVersions.GetValueOrDefault((execution.TenantId, execution.WorkflowId, execution.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
        var step = workflowSteps.GetValueOrDefault((execution.TenantId, execution.ExecutionId, witness.NodeId)) ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        if (witness.WorkflowId != execution.WorkflowId || witness.Version != execution.Version || witness.GraphHash != execution.GraphHash
            || witness.Causation != execution.Source.Causation
            || step.OperationId != witness.OperationId || step.TaskId != task.TaskId || step.Status < WorkflowOperationStatus.Admitted
            || task.Source.InstanceId != execution.CoordinatorInstanceId || task.Source.OriginalInitiatorPrincipalId != execution.Source.OriginalHumanPrincipalId)
            throw new AccessDeniedException("workflow_task_source_unavailable");
        WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct, dispatch).GetAwaiter().GetResult();
        var coordinator = FindTenantAgentAsync(execution.TenantId, task.Source.InstanceId, ct).Result ?? throw new AccessDeniedException("workflow_coordinator_unavailable");
        var target = FindTenantAgentAsync(execution.TenantId, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
        if (coordinator.PrincipalId != task.Source.PrincipalId) throw new AccessDeniedException("workflow_coordinator_unavailable");
        WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(coordinator), coordinator.Instance.SettingsOverrides), target, new(this), ct).GetAwaiter().GetResult();
        TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct).GetAwaiter().GetResult();
    }
    public ValueTask<TaskMutationResult> AdmitWorkflowTaskAsync(WorkflowTaskAdmission command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var tenant = command.TenantId; var task = command.Task; var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("tenant_unavailable");
            var execution = workflowExecutions.GetValueOrDefault((tenant, command.ExecutionId)) ?? throw new AccessDeniedException("workflow_unavailable");
            var version = workflowVersions[(tenant, execution.WorkflowId, execution.Version)];
            WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
            var operation = workflowSteps.Values.SingleOrDefault(o => o.TenantId == tenant && o.ExecutionId == execution.ExecutionId && o.OperationId == command.OperationId)
                ?? throw new AccessDeniedException("workflow_step_unavailable");
            var previous = operation.TaskId is { } previousId ? delegatedTasks.GetValueOrDefault((tenant, previousId)) : null;
            if (previous is not null)
            {
                RequireWorkflowTaskSource(previous, state, ct);
                if (previous.Source.Workflow?.OperationId != command.OperationId || previous.Request.Fingerprint() != task.Request.Fingerprint()) throw new SecurityConflictException("workflow_task_request_conflict");
                return ValueTask.FromResult(new TaskMutationResult(previous, state.Tenant.Revision, true));
            }
            if (state.Tenant.Revision != command.ExpectedSecurityRevision || operation.Revision != command.ExpectedOperationRevision || operation.Status != WorkflowOperationStatus.Reserved)
                throw new SecurityConflictException("stale_revision");
            if (execution.CoordinatorInstanceId is not { } coordinatorId) throw new AccessDeniedException("workflow_coordinator_unavailable");
            var coordinator = FindTenantAgentAsync(tenant, coordinatorId, ct).Result ?? throw new AccessDeniedException("workflow_coordinator_unavailable");
            var target = FindTenantAgentAsync(tenant, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
            WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(coordinator), coordinator.Instance.SettingsOverrides), target, new(this), ct).GetAwaiter().GetResult();
            if (task.Source.PrincipalId != coordinator.PrincipalId || task.CreatedAt > TaskNow || task.CreatedAt < operation.CreatedAt || task.DeadlineAt > execution.DeadlineAt)
                throw new AccessDeniedException("workflow_task_admission_mismatch");
            WorkflowExecutionAuthority.TaskGraph(command, execution, operation, version, target);
            var all = delegatedTasks.Values.Where(t => t.Source.TenantId == tenant).ToArray(); TaskAuthority.Bounds(task, all, TaskNow, target.Instance.Cooperation!);
            if (task.Assignment.Budget != WorkflowTaskRules.Allocate(execution, version, operation.NodeId, command.Run.Admission.Configuration!.Definition, target.Instance.ExecutionBudgets, all))
                throw new SecurityConflictException("workflow_task_budget_changed");
            TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct).GetAwaiter().GetResult();
            RequireSpaceExecutionSource(command.Run, state, new(this), ct, admission: true).GetAwaiter().GetResult();
            AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(command.Run.PinnedModel, modelSettings.GetValueOrDefault(tenant)?.Revision ?? 0);
            StageTaskGraph(new(tenant, command.Run.SessionId, target.Instance.InstanceId, target.PrincipalId, task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, task.CreatedAt), command.Session, command.Run);
            delegatedTasks.Add((tenant, task.TaskId), task);
            workflowSteps[(tenant, execution.ExecutionId, operation.NodeId)] = operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.Admitted };
            BumpTaskSecurity(state); _audit.Add(new(command.AuditId, tenant, coordinator.PrincipalId, "tasks.delegate", task.TaskId.ToString("D"), "allowed", "workflow_continuation", TaskNow));
            return ValueTask.FromResult(new TaskMutationResult(task, state.Tenant.Revision + 1));
        }
    }
}
