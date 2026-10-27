using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<TaskConsentAuthority?> TaskConsent(AgentCoreDbContext db, AgentCore.Domain.Tasks.DelegatedTask task, TenantSecurityState state, CancellationToken ct)
    {
        var taskId = task.TaskId.ToString("D");
        var row = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"SELECT * FROM TenantApprovalRequests WHERE TenantId={task.Source.TenantId} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.TaskId')={taskId}").AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var request = row.Domain();
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct);
        if (TenantTaskConsentRules.IsHuman(request))
        {
            var humanOutcome = await db.Set<HumanTaskApprovalOutcomeRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.OperationId == request.Operation.OperationId, ct);
            return await TaskConsentAuthority.VerifyHumanAsync(task, request, decision, humanOutcome is null ? null : HumanTaskApprovalPersistence.Value(humanOutcome), state, new(this), ct);
        }
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct);
        var receipt = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.OperationId == request.Operation.OperationId, ct);
        return await TaskConsentAuthority.VerifyAsync(task, request, decision, grant, receipt?.Domain(), state, new(this), ct);
    }
    private async Task<(TenantSession Source, AgentRun Original, TenantAgent Worker)> RequireTaskConsentOperation(AgentCoreDbContext db,
        TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct, bool creation = false)
    {
        var p = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_review_unavailable"); p.Validate();
        var task = p.Task; var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var source = await ReadSandboxSession(db, request.TenantId, task.Source.SessionId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var originalRow = task.Source.RunId is { } id ? await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), ct) : null;
        if (originalRow is null) throw new AccessDeniedException("task_source_unavailable");
        var original = AgentRunStoreMapping.ToDomain(originalRow);
        await TenantTaskConsentRules.RequireModelSourceAsync(request, run, original, source, agent,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides), state, new(this), TaskNow, ct, creation);
        var worker = await RequireTaskConsentWorker(db, task, state, ct);
        return (source, original, worker);
    }
    private async Task<TenantAgent> RequireTaskConsentWorker(AgentCoreDbContext db, AgentCore.Domain.Tasks.DelegatedTask task, TenantSecurityState state, CancellationToken ct)
    {
        var workerRow = await OwnedAgents(db, task.Source.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("task_target_unavailable"); var worker = Owned(workerRow);
        await TaskAuthority.TargetAsync(state, task.Source.PrincipalId, worker, new(this), ct);
        if (worker.Instance.Revision != task.Assignment.InstanceRevision || worker.PrincipalId != task.Assignment.PrincipalId || task.DeadlineAt <= TaskNow)
            throw new AccessDeniedException("task_assignment_changed");
        var space = await db.Set<SpaceRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == task.Source.TenantId && s.SpaceId == task.Assignment.SpaceId && s.DeletedAt == null, ct);
        var instance = worker.Instance.InstanceId.ToString("D");
        if (space is null || space.Kind == SpaceKind.Private && space.OwnerInstanceId != instance
            || !await db.Set<SpaceMembershipRecord>().AnyAsync(m => m.TenantId == task.Source.TenantId && m.SpaceId == task.Assignment.SpaceId && m.InstanceId == instance, ct))
            throw new AccessDeniedException("space_membership_unavailable");
        if (task.Assignment.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != task.Assignment.EnvironmentSelectionEpoch
            || !await db.Set<SpaceEnvironmentRecord>().AnyAsync(e => e.TenantId == task.Source.TenantId && e.SpaceId == space.SpaceId && e.EnvironmentId == environment && e.DeletedAt == null, ct)))
            throw new AccessDeniedException("environment_unavailable");
        await TenantSpaceContextResolver.RequireCurrentPermissionsAsync(state, worker.PrincipalId, space.SpaceId, task.Assignment.EnvironmentId is not null, new(this), ct);
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(task.Assignment.Model!, await db.Set<TenantModelSettingsRecord>()
            .Where(m => m.TenantId == task.Source.TenantId).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
        return worker;
    }
    private async Task<TenantAgent> ValidateApprovedTask(AgentCoreDbContext db, TenantApprovalRequest request, TaskAdmission admission,
        AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        TenantTaskConsentRules.MatchAdmission(request, admission);
        var current = await RequireTaskConsentOperation(db, request, run, agent, ct); var task = admission.Task;
        var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (admission.ExpectedSecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        if (await db.Set<DelegatedTaskRow>().AnyAsync(t => t.TenantId == request.TenantId && (t.TaskId == task.TaskId
            || t.OperationScope == task.Source.OperationScope && t.RequestKey == task.Request.RequestKey), ct)) throw new SecurityConflictException("task_request_conflict");
        TaskAdmissionFactory.RequireGraph(admission, current.Source, current.Original, current.Worker, TaskNow);
        var all = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == request.TenantId).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        TaskAuthority.Bounds(task, all, TaskNow, current.Worker.Instance.Cooperation!);
        await RequireTaskBudget(db, admission, run, current.Worker, ct, task.Source.RunId);
        var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), Tool: "tasks.delegate");
        foreach (var dependency in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
            if (!await CanReadTaskAsync(db, state, actor, all.Single(t => t.TaskId == dependency), ct))
                throw new AccessDeniedException("task_dependency_unavailable");
        if (admission.WorkerRun is { } worker) await RequireSpaceExecutionSource(db, worker, state, new(this), ct, admission: true);
        return current.Worker;
    }
    private static void StageApprovedTask(AgentCoreDbContext db, TaskAdmission admission, TenantAgent worker)
    {
        var task = admission.Task;
        if (admission.WorkerRun is { } run)
            StageTaskGraph(db, new(admission.TenantId, run.SessionId, worker.Instance.InstanceId, worker.PrincipalId,
                task.Source.OriginalInitiatorPrincipalId ?? worker.PrincipalId, task.CreatedAt), admission.WorkerSession!, run);
        db.Add(TaskPersistence.Row(task));
        foreach (var dependency in task.Request.DependsOnTaskIds)
            db.Add(new TaskDependencyRow { TenantId = admission.TenantId, TaskId = task.TaskId, PrerequisiteId = dependency });
    }
}
