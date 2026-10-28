using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkflowExecutionStore
{
    private WorkflowEvidenceAuthority WorkflowEvidence() => new(this, this, this, this, this, this, this, new(this), clock);
    public async ValueTask<WorkflowExecution?> FindWorkflowExecutionAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<WorkflowExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == execution, ct); return row is null ? null : WorkflowPersistence.Read<WorkflowExecution>(row.Json); }
    public async ValueTask<WorkflowExecution?> FindWorkflowStartRequestAsync(Guid tenant, string scope, string key, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<WorkflowExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.SourceScope == scope && r.RequestKey == key, ct); return row is null ? null : WorkflowPersistence.Read<WorkflowExecution>(row.Json); }
    public async ValueTask<WorkflowPage<WorkflowExecution>> ListWorkflowExecutionsAsync(Guid tenant, IReadOnlyList<Guid>? allowedWorkflows, int page, int size, CancellationToken ct = default, Guid? reader = null)
    {
        if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<WorkflowExecutionRow>().AsNoTracking().Where(r => r.TenantId == tenant && (allowedWorkflows == null || allowedWorkflows.Contains(r.WorkflowId)));
        if (reader is { } actor)
        {
            var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            var visible = new List<WorkflowExecution>(); var count = 0;
            await foreach (var row in query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.ExecutionId).AsAsyncEnumerable().WithCancellation(ct))
            {
                var value = WorkflowPersistence.Read<WorkflowExecution>(row.Json);
                if (!await WorkflowReadRules.CanReadAsync(state, actor, value, new(this), this, this, ct)) continue;
                if (count >= page * size && visible.Count < size) visible.Add(value); count++;
            }
            await tx.CommitAsync(ct); return new(visible, count, page, size);
        }
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.ExecutionId).Skip(page * size).Take(size).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(r => WorkflowPersistence.Read<WorkflowExecution>(r.Json)).ToArray(), total, page, size);
    }
    private static async Task<WorkflowVersion> WorkflowVersionAt(AgentCoreDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        var row = await db.Set<WorkflowVersionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == execution.TenantId && r.WorkflowId == execution.WorkflowId && r.Version == execution.Version, ct)
            ?? throw new AccessDeniedException("workflow_version_unavailable");
        return WorkflowPersistence.Read<WorkflowVersion>(row.Json);
    }
    public async ValueTask<WorkflowExecution> StartHumanWorkflowAsync(HumanWorkflowStart command, CancellationToken ct = default)
    {
        var value = command.Execution; await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireHumanSessionAsync(db, value.TenantId, command.Human, ct);
        var state = await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var version = await WorkflowVersionAt(db, value, ct); WorkflowExecutionRules.Validate(value, version);
        if (value.Source.Kind != WorkflowInvocationKind.HumanManual || value.Source.PrincipalId != command.Human.PrincipalId || value.Source.AccountId != command.Human.AccountId
            || value.Source.SourceId != value.ExecutionId || value.Revision != 1 || value.Status != WorkflowExecutionStatus.PendingAdmission)
            throw new AccessDeniedException("workflow_source_mismatch");
        await WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, this, this, new(this), TaskNow, ct);
        var previous = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.SourceScope == value.Source.RequestScope && r.RequestKey == value.RequestKey, ct);
        if (previous is not null) { var saved = WorkflowPersistence.Read<WorkflowExecution>(previous.Json); if (saved.Fingerprint != value.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return saved; }
        var definition = await db.Set<WorkflowDefinitionRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.WorkflowId == value.WorkflowId, ct);
        if (definition is null || WorkflowPersistence.Read<WorkflowDefinition>(definition.Json).Retired) throw new AccessDeniedException("workflow_version_unavailable");
        await RequireWorkflowParticipants(db, state, value, version, ct);
        if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        if (await db.Set<WorkflowExecutionRow>().CountAsync(r => r.TenantId == value.TenantId && r.Status < (int)WorkflowExecutionStatus.Completed, ct) >= 32)
            throw new SecurityConflictException("workflow_capacity_exceeded");
        db.Add(WorkflowPersistence.Row(value)); await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, value.TenantId, command.Human.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "human_manual", TaskNow));
        await RequireHumanSessionAsync(db, value.TenantId, command.Human, ct); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return value;
    }
    public async ValueTask<bool> WriteWorkflowExecutionAsync(WorkflowExecutionWrite command, CancellationToken ct = default)
    {
        var value = command.Execution; await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.ExecutionId == value.ExecutionId, ct);
        if (row is null || row.Revision != command.ExpectedRevision) return false;
        var before = WorkflowPersistence.Read<WorkflowExecution>(row.Json); var version = await WorkflowVersionAt(db, value, ct);
        WorkflowExecutionAuthority.ExecutionChange(before, value, version);
            if (before.SourceClosure is null && value.SourceClosure is { } closure)
            {
                var reason = await WorkflowSourceClosureRules.ReasonAsync(before, await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable"), this, this, this, TaskNow, ct);
                if (reason != closure.ReasonCode || closure.At > TaskNow) throw new AccessDeniedException("workflow_source_closure_not_qualified");
            }
        if (value.Status == WorkflowExecutionStatus.Completed)
        {
            await WorkflowExecutionAuthority.RequireSourceAsync(await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable"), before, version, this, this, new(this), TaskNow, ct);
            var steps = await ReadWorkflowStepsAsync(value.TenantId, value.ExecutionId, ct);
            var state = await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            if (!steps.Any(s => s.Kind == WorkflowNodeKind.EndSuccess && s.Status == WorkflowOperationStatus.SignalConsumed && s.ProofAccepted)
                || !(await WorkflowEvidence().FinalProofAsync(state, before, version, steps, ct)).CanSucceed) throw new AccessDeniedException("workflow_completion_not_qualified");
        }
        db.Entry(row).CurrentValues.SetValues(WorkflowPersistence.Row(value));
        db.Add(new AuthorizationAudit(command.AuditId, value.TenantId, value.Source.PrincipalId, "workflows.observe", value.ExecutionId.ToString("D"), "recorded", value.Status.ToString(), TaskNow));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; } catch (DbUpdateConcurrencyException) { return false; }
    }
    public async ValueTask<IReadOnlyList<WorkflowStepOperation>> ReadWorkflowStepsAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var rows = await db.Set<WorkflowStepRow>().AsNoTracking().Where(r => r.TenantId == tenant && r.ExecutionId == execution).OrderBy(r => r.NodeId).Take(40).ToArrayAsync(ct); return rows.Select(r => WorkflowPersistence.Read<WorkflowStepOperation>(r.Json)).ToArray(); }
    public async ValueTask<IReadOnlyList<AgentCore.Domain.Tasks.DelegatedTask>> ReadWorkflowTasksAsync(Guid tenant, Guid execution, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var rows = await db.Set<DelegatedTaskRow>().AsNoTracking().Where(r => r.TenantId == tenant && r.WorkflowExecutionId == execution).Take(40).ToArrayAsync(ct); return rows.Select(TaskPersistence.Value).ToArray(); }
    public async ValueTask<WorkflowStepOperation> ReserveWorkflowStepAsync(WorkflowStepReservation command, CancellationToken ct = default)
    {
        var value = command.Operation; await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId, ct) ?? throw new AccessDeniedException("workflow_unavailable");
        var execution = WorkflowPersistence.Read<WorkflowExecution>(row.Json); var version = await WorkflowVersionAt(db, execution, ct);
        var state = await ReadTenantAsync(db, command.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct);
        WorkflowExecutionRules.Operation(execution, value, version);
        var previous = await db.Set<WorkflowStepRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId && r.NodeId == value.NodeId, ct);
        if (previous is not null) return WorkflowPersistence.Read<WorkflowStepOperation>(previous.Json);
        if (row.Revision != command.ExpectedExecutionRevision || value.Status != WorkflowOperationStatus.Reserved || value.Revision != 1
            || value.Outcome is not null || value.ProofAccepted || value.EvaluationAttemptId is not null || value.DecisionActorId is not null)
            throw new SecurityConflictException("workflow_step_conflict");
        db.Add(WorkflowPersistence.Row(value)); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return value;
    }
    public async ValueTask<bool> WriteWorkflowStepAsync(WorkflowStepWrite command, CancellationToken ct = default)
    {
        var value = command.Operation; await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<WorkflowStepRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.ExecutionId == value.ExecutionId && r.NodeId == value.NodeId, ct);
        if (row is null || row.Revision != command.ExpectedRevision) return false;
        var executionRow = await db.Set<WorkflowExecutionRow>().SingleAsync(r => r.TenantId == value.TenantId && r.ExecutionId == value.ExecutionId, ct);
        var execution = WorkflowPersistence.Read<WorkflowExecution>(executionRow.Json); var version = await WorkflowVersionAt(db, execution, ct);
        var state = await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct);
        var before = WorkflowPersistence.Read<WorkflowStepOperation>(row.Json);
        var taskRow = before.TaskId is { } id ? await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.TaskId == id, ct) : null;
        var task = taskRow is null ? null : TaskPersistence.Value(taskRow);
        var worker = task?.InitialWorkerRunId is not null ? await TaskFinalRun(db, task, ct) : null;
        if (value.Kind == WorkflowNodeKind.AgentTask) WorkflowStepEvidenceRules.RequireWrite(execution, version, before, value, task, worker);
        else await WorkflowEvidence().RequireStepWriteAsync(state, execution, version, before, value,
            await ReadWorkflowStepsAsync(value.TenantId, value.ExecutionId, ct), ct);
        db.Entry(row).CurrentValues.SetValues(WorkflowPersistence.Row(value)); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return true;
    }
    public async ValueTask<WorkflowProcessDecision?> FindWorkflowDecisionAsync(Guid tenant, Guid execution, string node, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<WorkflowDecisionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == execution && r.NodeId == node, ct); return row is null ? null : WorkflowPersistence.Read<WorkflowProcessDecision>(row.Json); }
    public async ValueTask<WorkflowProcessDecision> DecideWorkflowAsync(WorkflowDecisionCommand command, CancellationToken ct = default)
    {
        var fingerprint = WorkflowDecisionRules.Fingerprint(command); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireHumanSessionAsync(db, command.TenantId, command.Human, ct);
        var executionRow = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId, ct) ?? throw new AccessDeniedException("workflow_unavailable");
        var execution = WorkflowPersistence.Read<WorkflowExecution>(executionRow.Json); var version = await WorkflowVersionAt(db, execution, ct);
        var state = await ReadTenantAsync(db, command.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await WorkflowExecutionAuthority.RequireSourceAsync(state, execution, version, this, this, new(this), TaskNow, ct, dispatch: false);
        if (!await WorkflowReadRules.CanReadAsync(state, command.Human.PrincipalId, execution, new(this), this, this, ct)) throw new AccessDeniedException("resource_unavailable");
        await AgentCore.Application.Tasks.TaskAuthority.RequireAsync(state, command.Human.PrincipalId, "workflows.decide", WorkflowCatalogAuthority.Scope(command.TenantId, execution.WorkflowId), new(this), ct);
        var previous = await db.Set<WorkflowDecisionRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ActorId == command.Human.PrincipalId && r.RequestKey == command.RequestKey, ct);
        if (previous is not null) { var saved = WorkflowPersistence.Read<WorkflowProcessDecision>(previous.Json); if (saved.Fingerprint != fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return saved; }
        if (await db.Set<WorkflowDecisionRow>().AnyAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId && r.NodeId == command.NodeId, ct)) throw new SecurityConflictException("workflow_decision_already_recorded");
        var stepRow = await db.Set<WorkflowStepRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId && r.NodeId == command.NodeId, ct) ?? throw new AccessDeniedException("workflow_decision_unavailable");
        var step = WorkflowPersistence.Read<WorkflowStepOperation>(stepRow.Json);
        var receipt = await WorkflowDecisionRules.RequireAsync(command, state, execution, version, step, new(this), TaskNow, ct);
        db.Add(new WorkflowDecisionRow { TenantId = command.TenantId, ExecutionId = command.ExecutionId, NodeId = command.NodeId, ActorId = command.Human.PrincipalId, RequestKey = command.RequestKey, Json = WorkflowPersistence.Write(receipt) });
        db.Entry(stepRow).CurrentValues.SetValues(WorkflowPersistence.Row(WorkflowDecisionRules.Settle(step, receipt))); await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, command.TenantId, command.Human.PrincipalId, "workflows.decide", command.ExecutionId.ToString("D"), "recorded", receipt.Approved ? "approve" : "reject", TaskNow));
        await RequireHumanSessionAsync(db, command.TenantId, command.Human, ct); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return receipt;
    }
    public async ValueTask<WorkflowExecution> CancelWorkflowAsync(WorkflowCancelCommand command, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireHumanSessionAsync(db, command.TenantId, command.Human, ct);
        var row = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == command.TenantId && r.ExecutionId == command.ExecutionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var before = WorkflowPersistence.Read<WorkflowExecution>(row.Json); var state = await ReadTenantAsync(db, command.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (!await WorkflowReadRules.CanReadAsync(state, command.Human.PrincipalId, before, new(this), this, this, ct)) throw new AccessDeniedException("resource_unavailable");
        var next = await WorkflowCancellationRules.RequireAsync(command, state, before, new(this), TaskNow, ct);
        if (next == before) return before;
        WorkflowExecutionAuthority.ExecutionChange(before, next, await WorkflowVersionAt(db, before, ct));
        db.Entry(row).CurrentValues.SetValues(WorkflowPersistence.Row(next)); await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, command.TenantId, command.Human.PrincipalId, "workflows.cancel", command.ExecutionId.ToString("D"), "allowed", "cancellation_requested", TaskNow));
        await RequireHumanSessionAsync(db, command.TenantId, command.Human, ct); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return next;
    }
    private int workflowRecoveryOffset;
    public async ValueTask<IReadOnlyList<WorkflowExecution>> ListWorkflowRecoveryAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var bounded = Math.Clamp(limit, 1, 32);
        var query = db.Set<WorkflowExecutionRow>().AsNoTracking().Where(r => r.Status < (int)WorkflowExecutionStatus.Completed).OrderBy(r => r.CreatedAt).ThenBy(r => r.ExecutionId);
        var rows = await query.Skip(workflowRecoveryOffset).Take(bounded).ToArrayAsync(ct); workflowRecoveryOffset = rows.Length < bounded ? 0 : workflowRecoveryOffset + bounded;
        return rows.Select(r => WorkflowPersistence.Read<WorkflowExecution>(r.Json)).ToArray();
    }
}
