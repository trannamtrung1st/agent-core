using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TaskBudgetReservation> ProposeTaskBudgetAsync(Guid tenant, Guid sourceSession, TaskActor actor, ExecutionBudgetProfile workerBudget, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct, bool reportOnCompletion = false)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var (_, run, _) = RequireTaskSource(tenant, sourceSession, actor, TaskAuthority.PreparationPermission(actor, purpose), ct);
            if (run is null) return ValueTask.FromResult(TaskBudgetRules.Allocate(null, workerBudget, [], _ => null, TaskNow, reportOnCompletion: reportOnCompletion));
            return ValueTask.FromResult(TaskBudgetRules.Allocate(run, workerBudget, delegatedTasks.Values.Where(t => t.Source.TenantId == tenant).ToArray(), id => sessionBacking.ReadRetainedTenantOperationRun(tenant, id), TaskNow, reportOnCompletion: reportOnCompletion, workflowCharge: AgentCore.Application.Workflows.WorkflowBudgetRules.Charge(run, workflowExecutions.Values)));
        }
    }
    public ValueTask<TaskExecutionAllowance?> ReadTaskExecutionAllowanceAsync(AgentRun run, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (run.Admission.TaskReport is not null) return ValueTask.FromResult<TaskExecutionAllowance?>(null);
            var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
            var current = sessionBacking.ReadTenantRun(tenant, run.AgentRunId) ?? throw new AccessDeniedException("run_unavailable");
            if (current.Owner != run.Owner || current.Claim?.Generation != run.Claim?.Generation) throw new AccessDeniedException("tenant_runtime_unavailable");
            var all = delegatedTasks.Values.Where(t => t.Source.TenantId == tenant).ToArray();
            var workflowCharge = AgentCore.Application.Workflows.WorkflowBudgetRules.Charge(current, workflowExecutions.Values);
            if (workflowCharge.MaxSteps == 0 && !all.Any(t => t.Source.SessionId == current.SessionId && t.Source.RootActivationId == current.Admission.Activation.RootActivationId && t.Source.ActorKind == TaskActorKind.Agent)) return ValueTask.FromResult<TaskExecutionAllowance?>(null);
            return ValueTask.FromResult<TaskExecutionAllowance?>(TaskBudgetRules.Allowance(current, all, id => sessionBacking.ReadRetainedTenantOperationRun(tenant, id), workflowCharge));
        }
    }
    private void RequireTaskBudget(TaskAdmission admission, AgentRun? source, TenantAgent target, Guid? logicalSourceRunId = null)
    {
        var budget = ExecutionBudgetResolver.Resolve(admission.WorkerRun?.Admission.Configuration?.Definition
            ?? AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(target), target.Instance.SettingsOverrides), target.Instance.ExecutionBudgets, false).Profile;
        if (source is null)
        {
            if (TaskBudgetRules.Allocate(null, budget, [], _ => null, TaskNow, reportOnCompletion: admission.Task.Request.ReportOnCompletion) != admission.Task.Assignment.Budget)
                throw new SecurityConflictException("task_budget_changed");
            return;
        }
        var expected = TaskBudgetRules.Allocate(source, budget, delegatedTasks.Values.Where(t => t.Source.TenantId == admission.TenantId).ToArray(), id => sessionBacking.ReadRetainedTenantOperationRun(admission.TenantId, id), TaskNow, logicalSourceRunId, admission.Task.Request.ReportOnCompletion, AgentCore.Application.Workflows.WorkflowBudgetRules.Charge(source, workflowExecutions.Values));
        if (expected != admission.Task.Assignment.Budget) throw new SecurityConflictException("task_budget_changed");
    }
}
