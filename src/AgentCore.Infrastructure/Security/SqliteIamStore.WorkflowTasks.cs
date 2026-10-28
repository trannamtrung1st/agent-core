using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkflowTaskStore
{
    private async Task<bool> TaskSourceAvailable(AgentCoreDbContext db, DelegatedTask task, CancellationToken ct, bool dispatch = true)
    {
        if (task.Source.Workflow is not null)
        {
            try { var state = await ReadTenantAsync(db, task.Source.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable"); await RequireWorkflowTaskSource(db, task, state, ct, dispatch); return true; }
            catch (AccessDeniedException) { return false; }
        }
        var source = await ReadSandboxSession(db, task.Source.TenantId, task.Source.RequireSessionId(), ct);
        return source is not null && !AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow);
    }
    private async Task RequireWorkflowTaskSource(AgentCoreDbContext db, DelegatedTask task, AgentCore.Domain.Security.TenantSecurityState state, CancellationToken ct, bool dispatch = true)
    {
        var witness = task.Source.Workflow ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        var executionRow = await db.Set<WorkflowExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == task.Source.TenantId && e.ExecutionId == witness.ExecutionId, ct)
            ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        var execution = WorkflowPersistence.Read<WorkflowExecution>(executionRow.Json); var version = await WorkflowVersionAt(db, execution, ct);
        var stepRow = await db.Set<WorkflowStepRow>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == execution.TenantId && s.ExecutionId == execution.ExecutionId && s.NodeId == witness.NodeId, ct)
            ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        var step = WorkflowPersistence.Read<WorkflowStepOperation>(stepRow.Json);
        if (witness.WorkflowId != execution.WorkflowId || witness.Version != execution.Version || witness.GraphHash != execution.GraphHash
            || witness.Causation != execution.Source.Causation
            || step.OperationId != witness.OperationId || step.TaskId != task.TaskId || step.Status < WorkflowOperationStatus.Admitted
            || task.Source.InstanceId != execution.CoordinatorInstanceId || task.Source.OriginalInitiatorPrincipalId != execution.Source.OriginalHumanPrincipalId)
            throw new AccessDeniedException("workflow_task_source_unavailable");
        await WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct, dispatch);
        var coordinatorRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Source.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_coordinator_unavailable");
        var targetRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("task_target_unavailable");
        var coordinator = Owned(coordinatorRow);
        if (coordinator.PrincipalId != task.Source.PrincipalId) throw new AccessDeniedException("workflow_coordinator_unavailable");
        await WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, coordinator, ct), coordinator.Instance.SettingsOverrides), Owned(targetRow), new(this), ct);
        await TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct);
    }
    public async ValueTask<TaskMutationResult> AdmitWorkflowTaskAsync(WorkflowTaskAdmission command, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = command.TenantId; var task = command.Task;
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var executionRow = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == command.ExecutionId, ct)
            ?? throw new AccessDeniedException("workflow_unavailable");
        var execution = WorkflowPersistence.Read<WorkflowExecution>(executionRow.Json); var version = await WorkflowVersionAt(db, execution, ct);
        await WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct);
        var operationRow = await db.Set<WorkflowStepRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == command.ExecutionId && r.OperationId == command.OperationId, ct)
            ?? throw new AccessDeniedException("workflow_step_unavailable");
        var operation = WorkflowPersistence.Read<WorkflowStepOperation>(operationRow.Json);
        var previous = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.TaskId == operation.TaskId, ct);
        if (previous is not null)
        {
            var saved = TaskPersistence.Value(previous); await RequireWorkflowTaskSource(db, saved, state, ct);
            if (saved.Source.Workflow?.OperationId != command.OperationId || saved.Request.Fingerprint() != task.Request.Fingerprint()) throw new SecurityConflictException("workflow_task_request_conflict");
            return new(saved, state.Tenant.Revision, true);
        }
        if (state.Tenant.Revision != command.ExpectedSecurityRevision || operation.Revision != command.ExpectedOperationRevision || operation.Status != WorkflowOperationStatus.Reserved)
            throw new SecurityConflictException("stale_revision");
        if (execution.CoordinatorInstanceId is not { } coordinatorId) throw new AccessDeniedException("workflow_coordinator_unavailable");
        var coordinatorRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == coordinatorId.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_coordinator_unavailable"); var coordinator = Owned(coordinatorRow);
        var targetRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("task_target_unavailable"); var target = Owned(targetRow);
        await WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, coordinator, ct), coordinator.Instance.SettingsOverrides), target, new(this), ct);
        if (task.Source.PrincipalId != coordinator.PrincipalId || task.CreatedAt > TaskNow || task.CreatedAt < operation.CreatedAt || task.DeadlineAt > execution.DeadlineAt)
            throw new AccessDeniedException("workflow_task_admission_mismatch");
        WorkflowExecutionAuthority.TaskGraph(command, execution, operation, version, target);
        var all = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        TaskAuthority.Bounds(task, all, TaskNow, target.Instance.Cooperation!);
        if (task.Assignment.Budget != WorkflowTaskRules.Allocate(execution, version, operation.NodeId, command.Run.Admission.Configuration!.Definition, target.Instance.ExecutionBudgets, all))
            throw new SecurityConflictException("workflow_task_budget_changed");
        await TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct);
        await RequireSpaceExecutionSource(db, command.Run, state, new(this), ct, admission: true);
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(command.Run.PinnedModel,
            await db.Set<TenantModelSettingsRecord>().Where(m => m.TenantId == tenant).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
        StageTaskGraph(db, new(tenant, command.Run.SessionId, target.Instance.InstanceId, target.PrincipalId, task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, task.CreatedAt), command.Session, command.Run);
        db.Add(TaskPersistence.Row(task));
        var admitted = operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.Admitted };
        db.Entry(operationRow).CurrentValues.SetValues(WorkflowPersistence.Row(admitted)); await BumpTaskSecurity(db, state, ct);
        db.Add(new AgentCore.Domain.Security.AuthorizationAudit(command.AuditId, tenant, coordinator.PrincipalId, "tasks.delegate", task.TaskId.ToString("D"), "allowed", "workflow_continuation", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return new(task, state.Tenant.Revision + 1);
    }
}
