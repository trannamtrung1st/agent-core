using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask RequireTaskExecutionSourcesAsync(AgentCore.Domain.Conversation.AgentRun worker, CancellationToken ct = default)
    {
        var task = worker.Admission.DelegatedTask is { } work ? await FindTaskAsync(worker.Owner.TenantId!.Value, work.TaskId, ct) : null;
        if (task is null) throw new AccessDeniedException("task_source_unavailable");
        await using var db = await contexts.CreateDbContextAsync(ct); var tenant = task.Source.TenantId;
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (task.Source.Workflow is not null) { await RequireWorkflowTaskSource(db, task, state, ct); return; }
        var source = await ReadSandboxSession(db, tenant, task.Source.RequireSessionId(), ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var senderRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Source.InstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var targetRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("task_target_unavailable");
        var sender = Owned(senderRow);
        var sourceRun = task.Source.RunId is { } sourceId ? await FindTenantRunAsync(tenant, sourceId, ct) : null;
        await TaskReadyRules.RequireSourceAsync(task, source, sourceRun, sender,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, sender, ct), sender.Instance.SettingsOverrides), state, new(this), TaskNow, ct, await TaskConsent(db, task, state, ct));
        await TaskAuthority.TargetAsync(state, task.Source.PrincipalId, Owned(targetRow), new(this), ct);
    }
    public async ValueTask<bool> AdmitReadyTaskAsync(ReadyTaskAdmission admission, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = admission.TenantId;
        var row = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == admission.TaskId, ct);
        if (row is null || row.State != (int)DelegatedTaskState.PendingDependencies || row.Revision != admission.ExpectedRevision) return false;
        var task = TaskPersistence.Value(row); var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var source = await ReadSandboxSession(db, tenant, task.Source.RequireSessionId(), ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var agentRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Source.InstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var agent = Owned(agentRow);
        var sourceRow = task.Source.RunId is { } sourceId ? await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == sourceId.ToString("D"), ct) : null;
        await TaskReadyRules.RequireSourceAsync(task, source, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow), agent,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides), state, new(this), TaskNow, ct, await TaskConsent(db, task, state, ct));
        var targetRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Assignment.InstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("task_target_unavailable"); var target = Owned(targetRow);
        await TaskAuthority.TargetAsync(state, task.Source.PrincipalId, target, new(this), ct);
        var dependencies = await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant && task.Request.DependsOnTaskIds.Contains(t.TaskId)).ToArrayAsync(ct);
        var reservedTaskId = task.TaskId.ToString("D");
        var reservation = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"SELECT * FROM TenantApprovalRequests WHERE TenantId={tenant} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.TaskId')={reservedTaskId}").AsNoTracking().SingleOrDefaultAsync(ct);
        if (reservation is not null && TenantTaskConsentRules.IsHuman(reservation.Domain()) && admission.Run.AgentRunId != reservation.Domain().RunId)
            throw new AccessDeniedException("task_admission_mismatch");
        var next = TaskReadyRules.Graph(admission, task, dependencies.Select(TaskPersistence.Value).ToArray(), target, TaskNow);
        await RequireSpaceExecutionSource(db, admission.Run, state, new(this), ct, admission: true);
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(admission.Run.PinnedModel,
            await db.Set<TenantModelSettingsRecord>().Where(m => m.TenantId == tenant).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
        StageTaskGraph(db, new(tenant, admission.Run.SessionId, target.Instance.InstanceId, target.PrincipalId, task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, admission.Run.CreatedAtUtc), admission.Session, admission.Run);
        db.Entry(row).CurrentValues.SetValues(TaskPersistence.Row(next)); await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(admission.AuditId, tenant, target.PrincipalId, "tasks.ready", task.TaskId.ToString("D"), "allowed", "dependencies_succeeded", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Ready, next);
        AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("dependency", task.CreatedAt, admission.Run.CreatedAtUtc); return true;
    }
}
