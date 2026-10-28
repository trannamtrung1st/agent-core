using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<TaskExecutionAllowance> WorkflowRootCharge(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var rows = await db.Set<WorkflowExecutionRow>().AsNoTracking().Where(e => e.TenantId == run.Owner.TenantId).ToArrayAsync(ct);
        return AgentCore.Application.Workflows.WorkflowBudgetRules.Charge(run, rows.Select(r => WorkflowPersistence.Read<AgentCore.Domain.Workflows.WorkflowExecution>(r.Json)));
    }
    private async Task<(DelegatedTask[] Tasks, Dictionary<Guid, AgentRun> Runs)> TaskBudgetFacts(AgentCoreDbContext db, Guid tenant, Guid? root, CancellationToken ct)
    {
        var tasks = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant && (root == null || t.RootActivationId == root)).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        var sessions = tasks.Select(t => t.WorkerSessionId?.ToString("D")).OfType<string>().ToArray();
        var rows = await db.AgentRuns.AsNoTracking().Where(r => r.TenantId == tenant && sessions.Contains(r.SessionId)).ToArrayAsync(ct);
        return (tasks, rows.Select(AgentRunStoreMapping.ToDomain).ToDictionary(r => r.AgentRunId));
    }
    public async ValueTask<TaskBudgetReservation> ProposeTaskBudgetAsync(Guid tenant, Guid sourceSession, TaskActor actor, ExecutionBudgetProfile workerBudget, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct, bool reportOnCompletion = false)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (_, run, _, _) = await RequireTaskSource(db, tenant, sourceSession, actor, TaskAuthority.PreparationPermission(actor, purpose), ct);
        if (run is null) return TaskBudgetRules.Allocate(null, workerBudget, [], _ => null, TaskNow, reportOnCompletion: reportOnCompletion);
        var facts = await TaskBudgetFacts(db, tenant, run?.Admission.Activation.RootActivationId, ct);
        return TaskBudgetRules.Allocate(run, workerBudget, facts.Tasks, id => facts.Runs.GetValueOrDefault(id), TaskNow, reportOnCompletion: reportOnCompletion, workflowCharge: await WorkflowRootCharge(db, run!, ct));
    }
    public async ValueTask<TaskExecutionAllowance?> ReadTaskExecutionAllowanceAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Admission.TaskReport is not null) return null;
        await using var db = await contexts.CreateDbContextAsync(ct); var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var row = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"), ct) ?? throw new AccessDeniedException("run_unavailable");
        var current = AgentRunStoreMapping.ToDomain(row);
        if (current.Owner != run.Owner || current.Claim?.Generation != run.Claim?.Generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var facts = await TaskBudgetFacts(db, tenant, current.Admission.Activation.RootActivationId, ct);
        var workflowCharge = await WorkflowRootCharge(db, current, ct);
        if (workflowCharge.MaxSteps == 0 && !facts.Tasks.Any(t => t.Source.SessionId == current.SessionId && t.Source.ActorKind == TaskActorKind.Agent)) return null;
        return TaskBudgetRules.Allowance(current, facts.Tasks, id => facts.Runs.GetValueOrDefault(id), workflowCharge);
    }
    private async Task RequireTaskBudget(AgentCoreDbContext db, TaskAdmission admission, AgentRun? source, TenantAgent target, CancellationToken ct, Guid? logicalSourceRunId = null)
    {
        var definition = admission.WorkerRun?.Admission.Configuration?.Definition ?? AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, target, ct), target.Instance.SettingsOverrides);
        var budget = ExecutionBudgetResolver.Resolve(definition, target.Instance.ExecutionBudgets, false).Profile;
        if (source is null)
        {
            if (TaskBudgetRules.Allocate(null, budget, [], _ => null, TaskNow, reportOnCompletion: admission.Task.Request.ReportOnCompletion) != admission.Task.Assignment.Budget)
                throw new SecurityConflictException("task_budget_changed");
            return;
        }
        var facts = await TaskBudgetFacts(db, admission.TenantId, source?.Admission.Activation.RootActivationId, ct);
        var expected = TaskBudgetRules.Allocate(source, budget, facts.Tasks, id => facts.Runs.GetValueOrDefault(id), TaskNow, logicalSourceRunId, admission.Task.Request.ReportOnCompletion, await WorkflowRootCharge(db, source!, ct));
        if (expected != admission.Task.Assignment.Budget) throw new SecurityConflictException("task_budget_changed");
    }
}
