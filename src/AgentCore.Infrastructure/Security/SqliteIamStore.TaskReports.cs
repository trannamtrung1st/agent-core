using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<TaskReportSource> TaskReportSource(AgentCoreDbContext db, Guid tenant, Guid taskId, CancellationToken ct)
    {
        var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.TaskId == taskId, ct)
            ?? throw new AccessDeniedException("task_report_unavailable"); var task = TaskPersistence.Value(row);
        var notice = await db.Set<TaskNotificationRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.TaskId == taskId, ct) ?? throw new AccessDeniedException("task_report_unavailable");
        var worker = task.TerminalRunId is { } terminal
            ? await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == terminal.ToString("D"), ct) : null;
        return new(task, TaskPersistence.Value<TaskNotification>(notice.Json), await ReadSandboxSession(db, tenant, task.Source.SessionId, ct), worker is null ? null : AgentRunStoreMapping.ToDomain(worker));
    }
    private async Task RequireTaskReport(AgentCoreDbContext db, TaskReportSource source, CancellationToken ct)
    {
        if (source.Task.WorkerSessionId is not { } worker || await ReadSandboxSession(db, source.Task.Source.TenantId, worker, ct) is not { } session
            || SessionRetention.IsDue(session.Snapshot, TaskNow)) throw new AccessDeniedException("task_report_source_unavailable");
        await RequireTenantOutputSources(db, source.Worker ?? throw new AccessDeniedException("task_report_source_unavailable"), ct);
        var state = await ReadTenantAsync(db, source.Task.Source.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await TaskReportRules.AuthorizeAsync(source, state, new(this), TaskNow, ct);
    }
    public async ValueTask<IReadOnlyList<TaskReportSource>> ListTaskReportsAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Set<TaskNotificationRow>().FromSqlRaw("""
            SELECT n.* FROM TenantTaskNotifications n JOIN TenantDelegatedTasks t ON t.TenantId=n.TenantId AND t.TaskId=n.TaskId
            WHERE json_extract(n.Json,'$.HandlingRunId') IS NULL AND json_extract(n.Json,'$.WithheldReason') IS NULL
                AND json_extract(t.Json,'$.Request.ReportOnCompletion')=1 ORDER BY n.NotificationId LIMIT {0}
            """, Math.Clamp(limit, 1, 32)).AsNoTracking().ToArrayAsync(ct);
        var result = new List<TaskReportSource>(); foreach (var row in rows) result.Add(await TaskReportSource(db, row.TenantId, row.TaskId, ct)); return result;
    }
    public async ValueTask WithholdTaskReportAsync(Guid tenant, Guid task, long noticeRevision, string reason, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<TaskNotificationRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.TaskId == task, ct); if (row is null) return;
        var notice = TaskPersistence.Value<TaskNotification>(row.Json);
        if (notice.HandlingRunId is not null || notice.WithheldReason is not null || notice.Revision != noticeRevision) return;
        notice = notice with { Revision = notice.Revision + 1, WithheldReason = TaskLimits.Text(reason, 160, "Report reason") };
        row.Revision = notice.Revision; row.Json = JsonSerializer.Serialize(notice); await SaveTaskChanges(db, ct);
        var retainedTask = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleAsync(t => t.TenantId == tenant && t.TaskId == task, ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.ReportWithheld, TaskPersistence.Value(retainedTask), reason);
    }
    public async ValueTask<bool> AdmitTaskReportAsync(TaskReportAdmission admission, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await TaskReportSource(db, admission.TenantId, admission.TaskId, ct);
        if (source.Notification.HandlingRunId is not null) return false;
        if (source.Notification.WithheldReason is not null || source.Notification.Revision != admission.ExpectedNoticeRevision) throw new SecurityConflictException("task_report_changed");
        await RequireTaskReport(db, source, ct); var report = admission.Report;
        var state = await ReadTenantAsync(db, admission.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var agentRow = await OwnedAgents(db, admission.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == source.Parent!.Owner.AgentInstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("executor_unavailable"); var agent = Owned(agentRow); TenantSessionRules.RequireExecutor(state, agent);
        TaskReportRules.Shape(source, report, agent, admission.ExpectedParentRevision, TaskNow);
        await RequireSpaceExecutionSource(db, report, state, new(this), ct, admission: true);
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(report.PinnedModel,
            await db.Set<TenantModelSettingsRecord>().Where(m => m.TenantId == admission.TenantId).Select(m => (long?)m.Revision).SingleOrDefaultAsync(ct) ?? 0);
        if (source.Parent!.Snapshot.PendingAgentInputIds.Count > 0 || await db.AgentRuns.AnyAsync(r => r.SessionId == report.SessionId.ToString("D")
            && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Failed && r.Status != (int)AgentRunStatus.Cancelled, ct)) throw new SecurityConflictException("parent_busy");
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(source.Parent.Snapshot, report)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(report));
        var noticeRow = await db.Set<TaskNotificationRow>().SingleAsync(n => n.TenantId == admission.TenantId && n.TaskId == admission.TaskId, ct);
        var notice = source.Notification with { Revision = source.Notification.Revision + 1, HandlingRunId = report.AgentRunId }; noticeRow.Revision = notice.Revision; noticeRow.Json = JsonSerializer.Serialize(notice);
        await BumpTaskSecurity(db, state, ct); db.Add(new AuthorizationAudit(admission.AuditId, admission.TenantId, agent.PrincipalId, "tasks.report", admission.TaskId.ToString("D"), "allowed", "parent_own_report_admitted", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.ReportAdmitted, source.Task);
        AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("report", source.Notification.CreatedAt, report.CreatedAtUtc); return true;
    }
    private async Task RequireTaskReportSources(AgentCoreDbContext db, AgentRun report, CancellationToken ct)
    {
        var work = report.Admission.TaskReport ?? throw new AccessDeniedException("task_report_unavailable"); var source = await TaskReportSource(db, report.Owner.TenantId!.Value, work.TaskId, ct);
        if (source.Notification.NotificationId != work.NotificationId || source.Notification.HandlingRunId is null || source.Task.Revision != work.TaskRevision
            || source.Task.TerminalRunId != work.TerminalRunId || source.Parent?.Owner.SessionId != report.SessionId
            || source.Parent.Owner.ExecutorPrincipalId != report.Owner.ExecutorPrincipalId || source.Parent.Owner.AgentInstanceId != report.AgentInstanceId)
            throw new AccessDeniedException("task_report_unavailable");
        await TaskReportRules.RequireHandlingRunAsync(source, report, (id, token) => FindTenantRunAsync(report.Owner.TenantId!.Value, id, token), ct);
        await RequireTaskReport(db, source, ct);
    }
    public async ValueTask RequireTaskOutcomeSourcesAsync(AgentRun worker, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, worker.Owner.TenantId!.Value).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == worker.AgentRunId.ToString("D"), ct);
        var current = row is null ? null : AgentRunStoreMapping.ToDomain(row);
        if (current is null || current.Owner != worker.Owner || current.Revision != worker.Revision || !current.IsTerminal
            || await ReadSandboxSession(db, worker.Owner.TenantId.Value, worker.SessionId, ct) is not { } session || SessionRetention.IsDue(session.Snapshot, TaskNow))
            throw new AccessDeniedException("task_result_source_unavailable");
        await RequireTenantOutputSources(db, current, ct);
    }
    public async ValueTask RequireTaskReportSourcesAsync(AgentRun report, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); await RequireTaskReportSources(db, report, ct); }
}
