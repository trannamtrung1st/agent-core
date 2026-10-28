using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantTaskStore
{
    private DateTimeOffset TaskNow => DateTimeOffset.FromUnixTimeMilliseconds((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
    public async ValueTask<IReadOnlyDictionary<Guid, int>> ReadTaskCapacityAsync(Guid tenant, IReadOnlyList<Guid> candidates, CancellationToken ct = default)
    {
        if (candidates.Count is < 1 or > 100) throw new ArgumentException("Use a bounded candidate set.");
        await using var db = await contexts.CreateDbContextAsync(ct); var keys = candidates.Select(id => id.ToString("D")).ToArray();
        var counts = await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant && keys.Contains(t.WorkerInstanceId) && t.State < (int)DelegatedTaskState.Blocked)
            .GroupBy(t => t.WorkerInstanceId).Select(g => new { Id = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        return candidates.Distinct().ToDictionary(id => id, id => counts.SingleOrDefault(c => c.Id == id.ToString("D"))?.Count ?? 0);
    }
    public async ValueTask<DelegatedTask?> FindTaskAsync(Guid tenant, Guid task, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == task, ct);
        if (row is null) return null;
        if (row.WorkflowExecutionId is { } execution) return await db.Set<WorkflowExecutionRow>().AnyAsync(e => e.TenantId == tenant && e.ExecutionId == execution, ct) ? TaskPersistence.Value(row) : null;
        return !await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == row.SourceSessionId, ct) ? null : TaskPersistence.Value(row);
    }
    private async ValueTask<bool> CanReadTaskAsync(AgentCoreDbContext db, TenantSecurityState state, TaskActor actor, DelegatedTask task, CancellationToken ct) =>
        (task.Source.Workflow is null || await TaskSourceAvailable(db, task, ct, dispatch: false))
        && await TaskAuthority.CanReadAsync(state, actor, task, new(this), ct, actor.Agent is { } frame
            ? await CompletionRun(db, state.Tenant.TenantId, frame.RunId, ct) : null);
    private async Task RequireTaskReader(AgentCoreDbContext db, Guid tenant, TaskActor actor, CancellationToken ct)
    {
        actor.Validate(); if (actor.Human is { } human) await RequireHumanSessionAsync(db, tenant, human, ct);
        else
        {
            var frame = actor.Agent!;
            if (frame.Owner.TenantId != tenant) throw new AccessDeniedException("resource_unavailable");
            await RequireTaskSource(db, tenant, frame.SessionId, actor, "sessions.read", ct, false);
        }
    }
    private async Task<(TenantSession Session, AgentRun? Run, TenantAgent Agent, TenantSecurityState State)> RequireTaskSource(
        AgentCoreDbContext db, Guid tenant, Guid source, TaskActor actor, string permission, CancellationToken ct, bool mutate = true, AgentCore.Domain.Security.AccessScope? permissionScope = null)
    {
        actor.Validate(); if (actor.Human is { } human) await RequireHumanSessionAsync(db, tenant, human, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var session = await ReadSandboxSession(db, tenant, source, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == session.Owner.AgentInstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(row);
        var runRow = actor.Agent is { } frame ? await OwnedRuns(db, tenant).SingleOrDefaultAsync(r => r.AgentRunId == frame.RunId.ToString("D"), ct) : null;
        var run = runRow is null ? null : AgentRunStoreMapping.ToDomain(runRow);
        await TaskAuthority.SourceAsync(state, actor, session, agent, run,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides), new(this), TaskNow, permission, mutate, ct, permissionScope);
        return (session, run, agent, state);
    }
    public async ValueTask<TaskMutationResult?> FindTaskRequestAsync(Guid tenant, Guid sourceSession, TaskActor actor, string requestKey, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (_, _, _, state) = await RequireTaskSource(db, tenant, sourceSession, actor, TaskAuthority.PreparationPermission(actor, purpose), ct);
        var scope = actor.Agent is { } frame ? $"run:{frame.RunId:D}" : $"human:{actor.PrincipalId:D}:session:{sourceSession:D}";
        var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.SourceSessionId == sourceSession.ToString("D")
            && t.OperationScope == scope && t.RequestKey == requestKey, ct);
        return row is null ? null : new(TaskPersistence.Value(row), state.Tenant.Revision, true);
    }
    public async ValueTask<TaskMutationResult> AdmitTaskAsync(TaskAdmission admission, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = admission.TenantId; var (source, run, _, state) = await RequireTaskSource(db, tenant, admission.SourceSessionId, admission.Actor, "tasks.delegate", ct);
        var task = admission.Task;
        var previous = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.SourceSessionId == admission.SourceSessionId.ToString("D")
            && t.OperationScope == task.Source.OperationScope && t.RequestKey == admission.Request.RequestKey, ct);
        if (previous is not null)
        {
            if (previous.RequestHash != admission.Request.Fingerprint()) throw new SecurityConflictException("task_request_conflict");
            return new(TaskPersistence.Value(previous), state.Tenant.Revision, true);
        }
        var scopeKey = task.Source.OperationScope; var requestKey = task.Request.RequestKey;
        if (await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"SELECT * FROM TenantApprovalRequests WHERE TenantId={tenant} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.OperationScope')={scopeKey} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Request.RequestKey')={requestKey}").AnyAsync(ct))
            throw new SecurityConflictException("task_approval_request_exists");
        if (state.Tenant.Revision != admission.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var targetRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("task_target_unavailable"); var target = Owned(targetRow);
        await TaskAuthority.TargetAsync(state, admission.Actor.PrincipalId, target, new(this), ct);
        TaskAdmissionFactory.RequireGraph(admission, source, run, target, TaskNow);
        var all = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        TaskAuthority.Bounds(task, all, TaskNow, target.Instance.Cooperation!);
        await RequireTaskReview(state, admission.Actor, task, ct);
        await RequireTaskBudget(db, admission, run, target, ct);
        foreach (var dependency in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
            if (!await CanReadTaskAsync(db, state, admission.Actor, all.Single(t => t.TaskId == dependency), ct)) throw new AccessDeniedException("task_dependency_unavailable");
        if (admission.WorkerRun is { } worker)
        {
            await RequireSpaceExecutionSource(db, worker, state, new(this), ct, admission: true);
            AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(worker.PinnedModel,
                await db.Set<TenantModelSettingsRecord>().Where(m => m.TenantId == tenant).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
            StageTaskGraph(db, new(tenant, worker.SessionId, worker.AgentInstanceId, target.PrincipalId,
                task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, task.CreatedAt), admission.WorkerSession!, worker);
        }
        db.Add(TaskPersistence.Row(task));
        foreach (var dependency in task.Request.DependsOnTaskIds) db.Add(new TaskDependencyRow { TenantId = tenant, TaskId = task.TaskId, PrerequisiteId = dependency });
        await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(admission.AuditId, tenant, admission.Actor.PrincipalId, "tasks.delegate", task.TaskId.ToString("D"), "allowed", "worker_own_admission", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Accepted, task);
        return new(task, state.Tenant.Revision + 1);
    }
    private ValueTask RequireTaskReview(TenantSecurityState state, TaskActor actor, DelegatedTask task, CancellationToken ct) =>
        TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct);
    private static async Task SaveTaskChanges(AgentCoreDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new SecurityConflictException("stale_revision"); }
        catch (DbUpdateException error) when (IsConstraint(error) || error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }) { throw new SecurityConflictException("task_write_conflict"); }
    }
    private static async Task BumpTaskSecurity(AgentCoreDbContext db, TenantSecurityState state, CancellationToken ct)
    {
        var row = await db.Set<Tenant>().SingleAsync(t => t.TenantId == state.Tenant.TenantId, ct);
        db.Entry(row).CurrentValues.SetValues(row with { Revision = checked(state.Tenant.Revision + 1) });
    }
    private static void StageTaskGraph(AgentCoreDbContext db, TenantSessionRoot root, SessionSnapshot session, AgentRun run)
    {
        db.Sessions.Add(SqliteMemoryStore.ToRecord(session)); db.Add(TenantSessionRecord.Row(root));
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(session, run)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
        db.ActivationSourceEntries.Add(new() { ActivationId = run.ActivationId.ToString("D"), SessionId = run.SessionId.ToString("D"),
            EntryId = session.Entries.Single().EntryId.ToString("D"), Ordinal = 0 });
    }
    public async ValueTask<TaskPage<DelegatedTask>> ReadTasksAsync(TaskQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > TaskLimits.MaxPage || query.Scope is not ("created" or "assigned" or "pending")) throw new ArgumentException("Invalid task page.");
        await using var db = await contexts.CreateDbContextAsync(ct); await RequireTaskReader(db, query.TenantId, query.Actor, ct);
        var state = await ReadTenantAsync(db, query.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var candidates = db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == query.TenantId);
        if (query.SessionId is { } session) candidates = query.Scope == "assigned" ? candidates.Where(t => t.WorkerSessionId == session.ToString("D")) : candidates.Where(t => t.SourceSessionId == session.ToString("D"));
        if (query.InstanceId is { } instance) candidates = query.Scope == "created" ? candidates.Where(t => db.Set<TenantSessionRecord>().Any(s => s.TenantId == query.TenantId && s.SessionId == t.SourceSessionId && s.AgentInstanceId == instance.ToString("D")))
            : candidates.Where(t => t.WorkerInstanceId == instance.ToString("D"));
        // Access filtering precedes the public page and cursor. Internal scans are bounded batches.
        var result = new List<DelegatedTask>(); Guid? after = query.Cursor;
        while (result.Count <= query.Limit)
        {
            var batch = await (after is { } cursor ? candidates.Where(t => t.TaskId.CompareTo(cursor) > 0) : candidates).OrderBy(t => t.TaskId).Take(100).ToArrayAsync(ct);
            if (batch.Length == 0) break;
            foreach (var row in batch)
            {
                var task = TaskPersistence.Value(row); after = row.TaskId;
                if (!await TaskSourceAvailable(db, task, ct, dispatch: false) || query.Scope == "pending" && TaskLimits.Terminal(task.State)
                    || !await CanReadTaskAsync(db, state, query.Actor, task, ct)) continue;
                result.Add(task); if (result.Count > query.Limit) break;
            }
            if (batch.Length < 100) break;
        }
        return new(result.Take(query.Limit).ToArray(), result.Count > query.Limit ? result[query.Limit - 1].TaskId : null, result.Count > query.Limit, state.Tenant.Revision);
    }
    public async ValueTask<TaskMutationResult> CancelTaskAsync(TaskCancel command, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = command.TenantId; await RequireTaskReader(db, tenant, command.Actor, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var row = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == command.TaskId, ct)
            ?? throw new AccessDeniedException("resource_unavailable"); var task = TaskPersistence.Value(row);
        if (!await CanReadTaskAsync(db, state, command.Actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
        if (command.Actor.Human is not null) await TaskAuthority.RequireAsync(state, command.Actor.PrincipalId, "sessions.manage",
            TenantSessionRules.Scope(tenant, task.Source.RequireSessionId()), new(this), ct);
        await TaskAuthority.RequireAsync(state, command.Actor.PrincipalId, "tasks.cancel", TaskAuthority.Scope(tenant, task.TaskId), new(this), ct);
        var requester = command.Actor.Agent is { } frame ? (await CompletionRun(db, tenant, frame.RunId, ct))?.Admission.ExecutionContext?.OriginalInitiatorPrincipalId : null;
        await TaskAuthority.RequireCurrentPolicyAsync(state, command.Actor, requester, "tasks.cancel", TaskAuthority.Scope(tenant, task.TaskId), new(this), ct);
        var hash = TaskLimits.Hash(new { command.TaskId, command.ExpectedRevision, command.Reason }); var key = TaskLimits.Key(command.RequestKey);
        var previous = await db.Set<TaskCommandRow>().SingleOrDefaultAsync(c => c.TenantId == tenant && c.TaskId == task.TaskId && c.ActorId == command.Actor.PrincipalId && c.RequestKey == key, ct);
        if (previous is not null)
        {
            if (previous.Hash != hash) throw new SecurityConflictException("task_request_conflict");
            return new(TaskPersistence.Value<DelegatedTask>(previous.Json), state.Tenant.Revision, true);
        }
        if (state.Tenant.Revision != command.ExpectedSecurityRevision || task.Revision != command.ExpectedRevision || TaskLimits.Terminal(task.State)) throw new SecurityConflictException("stale_revision");
        var at = TaskNow; var next = task.Advance(task.State == DelegatedTaskState.PendingDependencies ? DelegatedTaskState.Blocked : task.State, at,
            reason: task.State == DelegatedTaskState.PendingDependencies ? "cancelled_before_dependencies" : "cancellation_requested", cancellationRequested: true);
        if (task.WorkerSessionId is not null)
        {
            var worker = await TaskFinalRun(db, task, ct);
            if (!worker.IsTerminal)
            {
                var workerRow = await db.AgentRuns.SingleAsync(r => r.AgentRunId == worker.AgentRunId.ToString("D"), ct);
                AgentRunStoreMapping.Apply(workerRow, worker.RequestCancellation(worker.Revision, worker.KnownEffectSummary, at));
            }
        }
        db.Entry(row).CurrentValues.SetValues(TaskPersistence.Row(next));
        db.Add(new TaskCommandRow { TenantId = tenant, TaskId = task.TaskId, ActorId = command.Actor.PrincipalId, RequestKey = key, Hash = hash, Json = System.Text.Json.JsonSerializer.Serialize(next) });
        if (TaskLimits.Terminal(next.State) && task.Source.Workflow is null)
            db.Add(new TaskNotificationRow { TenantId = tenant, NotificationId = command.AuditId, TaskId = task.TaskId, TerminalRunId = next.TerminalRunId,
                RecipientSessionId = task.Source.RequireSessionId().ToString("D"), Revision = 1,
                Json = System.Text.Json.JsonSerializer.Serialize(new TaskNotification(tenant, command.AuditId, task.TaskId, next.TerminalRunId, task.Source.RequireSessionId(), 1, at)) });
        await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, tenant, command.Actor.PrincipalId, "tasks.cancel", task.TaskId.ToString("D"), "allowed", "cancellation_requested", at));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.CancellationRequested, next); return new(next, state.Tenant.Revision + 1);
    }
    private async Task<AgentRun> TaskFinalRun(AgentCoreDbContext db, DelegatedTask task, CancellationToken ct)
    {
        var rows = await OwnedRuns(db, task.Source.TenantId).AsNoTracking().Where(r => r.SessionId == task.WorkerSessionId!.Value.ToString("D")).ToArrayAsync(ct);
        var runs = rows.Select(AgentRunStoreMapping.ToDomain).ToDictionary(r => r.AgentRunId);
        return TaskSettlement.FinalRun(task, id => runs.GetValueOrDefault(id));
    }
    private int taskReconciliationOffset;
    public async ValueTask<IReadOnlyList<TaskReconciliation>> ListTaskReconciliationAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var candidates = db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.State < (int)DelegatedTaskState.Blocked).OrderBy(t => t.TaskId);
        var count = await candidates.CountAsync(ct); var take = Math.Clamp(limit, 1, 32);
        var offset = count == 0 ? 0 : (int)(unchecked((uint)Interlocked.Add(ref taskReconciliationOffset, take) - (uint)take) % (uint)count);
        var rows = await candidates.Skip(offset).Take(take).ToArrayAsync(ct);
        if (rows.Length < take && offset > 0) rows = rows.Concat(await candidates.Take(Math.Min(offset, take - rows.Length)).ToArrayAsync(ct)).ToArray();
        var result = new List<TaskReconciliation>();
        foreach (var row in rows)
        {
            var task = TaskPersistence.Value(row);
            var dependencies = await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == row.TenantId && task.Request.DependsOnTaskIds.Contains(t.TaskId)).ToArrayAsync(ct);
            result.Add(new(task, task.Source.SessionId is { } sourceId ? await ReadSandboxSession(db, row.TenantId, sourceId, ct) : null, task.InitialWorkerRunId is null ? null : await TaskFinalRun(db, task, ct), dependencies.Select(TaskPersistence.Value).ToArray()));
        }
        return result;
    }
    public async ValueTask<bool> ReconcileTaskAsync(Guid tenant, Guid taskId, long expectedRevision, Guid auditId, CancellationToken ct = default, string? readinessRefusal = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == taskId, ct);
        if (row is null || row.Revision != expectedRevision || TaskLimits.Terminal((DelegatedTaskState)row.State)) return false;
        var task = TaskPersistence.Value(row); var at = TaskNow; DelegatedTask next;
        if (task.InitialWorkerRunId is null)
        {
            var dependencies = await db.Set<DelegatedTaskRow>().Where(t => t.TenantId == tenant && task.Request.DependsOnTaskIds.Contains(t.TaskId)).ToArrayAsync(ct);
            if (readinessRefusal is not null || task.DeadlineAt <= at || dependencies.Any(t => TaskLimits.Terminal((DelegatedTaskState)t.State) && t.State != (int)DelegatedTaskState.Succeeded))
                next = task.Advance(DelegatedTaskState.Blocked, at, reason: readinessRefusal ?? (task.DeadlineAt <= at ? "task_deadline_exceeded" : "dependency_failed"));
            else return false;
        }
        else
        {
            var worker = await TaskFinalRun(db, task, ct);
            var target = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct);
            var stop = TaskSettlement.StopReason(task, worker, await TaskSourceAvailable(db, task, ct),
                target is not null && Owned(target).Instance.Lifecycle == AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active && Owned(target).Instance.Cooperation?.AcceptTasks == true, at);
            if (stop is not null && !worker.IsTerminal)
            {
                if (!worker.CancellationRequested)
                {
                    var workerRow = await db.AgentRuns.SingleAsync(r => r.AgentRunId == worker.AgentRunId.ToString("D"), ct);
                    worker = TenantRunCancellationRules.Cancel(worker, at); AgentRunStoreMapping.Apply(workerRow, worker);
                }
            }
            next = TaskSettlement.Observe(task, worker, at, stop); if (ReferenceEquals(next, task)) return false;
        }
        db.Entry(row).CurrentValues.SetValues(TaskPersistence.Row(next));
        if (TaskLimits.Terminal(next.State) && task.Source.SessionId is not null)
            db.Add(new TaskNotificationRow { TenantId = tenant, NotificationId = auditId, TaskId = taskId, TerminalRunId = next.TerminalRunId, RecipientSessionId = task.Source.RequireSessionId().ToString("D"), Revision = 1,
                Json = System.Text.Json.JsonSerializer.Serialize(new TaskNotification(tenant, auditId, taskId, next.TerminalRunId, task.Source.RequireSessionId(), 1, at)) });
        db.Add(new AuthorizationAudit(auditId, tenant, task.Assignment.PrincipalId, "tasks.settle", taskId.ToString("D"), "allowed", next.State.ToString(), at));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Settled, next, next.Reason); return true;
    }
}
