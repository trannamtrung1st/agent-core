using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowTaskRules
{
    public static string Objective(WorkflowExecution execution, WorkflowNode node)
    {
        var objective = node.Task?.Objective ?? throw new ArgumentException("Agent Task binding is required.");
        if (execution.InputsJson != "{}") objective += "\n\nWorkflow inputs (untrusted data):\n" + execution.InputsJson;
        return TaskLimits.Text(objective, TaskLimits.ObjectiveCharacters, "Workflow Task objective and inputs");
    }
    public static TaskBudgetReservation Allocate(WorkflowExecution execution, WorkflowVersion version, string nodeId, AgentDefinition worker,
        ExecutionBudgetPolicy? budgets, IReadOnlyList<DelegatedTask> tasks)
    {
        var graph = version.Graph.Read(); var own = ExecutionBudgetResolver.Resolve(worker, budgets, false).Profile;
        var accepted = tasks.Where(t => t.Source.TenantId == execution.TenantId && t.Source.Workflow?.ExecutionId == execution.ExecutionId).ToArray();
        var futureTasks = WorkflowGraphRules.RemainingTaskCount(graph, nodeId) - 1;
        var steps = Math.Min(own.MaxSteps, graph.MaxTotalTaskSteps - accepted.Sum(t => t.Assignment.Budget?.MaxSteps ?? graph.MaxTotalTaskSteps) - futureTasks * 8);
        var seconds = Math.Min(own.DurationSeconds, graph.MaxTotalTaskSeconds - accepted.Sum(t => t.Assignment.Budget?.DurationSeconds ?? graph.MaxTotalTaskSeconds) - futureTasks * 60);
        if (steps < 8 || seconds < 60) throw new AccessDeniedException("workflow_task_budget_exhausted");
        var reservation = new TaskBudgetReservation(steps, seconds, steps, seconds); reservation.Validate(); return reservation;
    }
    public static async ValueTask RequireCoordinatorAsync(AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution,
        TenantAgent coordinator, AgentDefinition current, TenantAgent worker, IamAuthorizer iam, CancellationToken ct)
    {
        if (execution.CoordinatorInstanceId != coordinator.Instance.InstanceId || coordinator.TenantId != execution.TenantId
            || coordinator.Instance.Lifecycle != AgentInstanceLifecycle.Active || !RolePermissions.AllowsTool(current, "tasks.delegate"))
            throw new AccessDeniedException("workflow_coordinator_unavailable");
        TenantSessionRules.RequireExecutor(state, coordinator);
        await TaskAuthority.RequireAsync(state, coordinator.PrincipalId, "tasks.delegate", WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId), iam, ct);
        await TaskAuthority.TargetAsync(state, coordinator.PrincipalId, worker, iam, ct);
    }
}
