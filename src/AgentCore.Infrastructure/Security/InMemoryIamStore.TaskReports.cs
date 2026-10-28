using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TaskReportSource TaskReportSource(Guid tenant, Guid taskId)
    {
        var task = delegatedTasks.GetValueOrDefault((tenant, taskId)) ?? throw new AccessDeniedException("task_report_unavailable");
        var notice = taskNotifications.GetValueOrDefault((tenant, taskId)) ?? throw new AccessDeniedException("task_report_unavailable");
        return new(task, notice, sessionBacking.ReadTenantSession(tenant, task.Source.RequireSessionId()),
            task.TerminalRunId is { } run ? sessionBacking.ReadTenantRun(tenant, run) : null);
    }
    private void RequireTaskReport(TaskReportSource source, CancellationToken ct)
    {
        if (source.Task.WorkerSessionId is not { } worker || sessionBacking.ReadTenantSession(source.Task.Source.TenantId, worker) is not { } session
            || SessionRetention.IsDue(session.Snapshot, TaskNow)) throw new AccessDeniedException("task_report_source_unavailable");
        RequireTenantOutputSources(source.Worker ?? throw new AccessDeniedException("task_report_source_unavailable"), ct);
        TaskReportRules.AuthorizeAsync(source, _tenants[source.Task.Source.TenantId], new(this), TaskNow, ct).AsTask().GetAwaiter().GetResult();
    }
    public ValueTask<IReadOnlyList<TaskReportSource>> ListTaskReportsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<TaskReportSource>>(taskNotifications.Values.Where(n => n.HandlingRunId is null && n.WithheldReason is null
                && delegatedTasks[(n.TenantId, n.TaskId)].Request.ReportOnCompletion).OrderBy(n => n.CreatedAt).ThenBy(n => n.NotificationId)
                .Take(Math.Clamp(limit, 1, 32)).Select(n => TaskReportSource(n.TenantId, n.TaskId)).ToArray());
    }
    public ValueTask WithholdTaskReportAsync(Guid tenant, Guid task, long noticeRevision, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (taskNotifications.GetValueOrDefault((tenant, task)) is { HandlingRunId: null, WithheldReason: null } notice && notice.Revision == noticeRevision)
            {
                taskNotifications[(tenant, task)] = notice with { Revision = notice.Revision + 1, WithheldReason = TaskLimits.Text(reason, 160, "Report reason") };
                AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.ReportWithheld, delegatedTasks[(tenant, task)], reason);
            }
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<bool> AdmitTaskReportAsync(TaskReportAdmission admission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var source = TaskReportSource(admission.TenantId, admission.TaskId);
            if (source.Notification.HandlingRunId is not null) return ValueTask.FromResult(false);
            if (source.Notification.WithheldReason is not null || source.Notification.Revision != admission.ExpectedNoticeRevision) throw new SecurityConflictException("task_report_changed");
            RequireTaskReport(source, ct); var report = admission.Report; var state = _tenants[admission.TenantId];
            var agent = FindTenantAgentAsync(admission.TenantId, source.Parent!.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            TenantSessionRules.RequireExecutor(state, agent);
            TaskReportRules.Shape(source, report, agent, admission.ExpectedParentRevision, TaskNow);
            RequireSpaceExecutionSource(report, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(report.PinnedModel, modelSettings.GetValueOrDefault(admission.TenantId)?.Revision ?? 0);
            var all = sessionBacking.AgentRuns;
            if (source.Parent.Snapshot.PendingAgentInputIds.Count > 0 || all.Runs.Values.Any(r => r.SessionId == report.SessionId && !r.IsTerminal)) throw new SecurityConflictException("parent_busy");
            if (all.Runs.ContainsKey(report.AgentRunId) || all.Activations.ContainsKey(report.ActivationId)
                || all.ByAdmission.ContainsKey((report.SessionId, report.Admission.Activation.DedupeKey)) || _audit.Any(a => a.AuditId == admission.AuditId)) throw new SecurityConflictException("identity_conflict");
            var activation = AgentRunStoreMapping.ToActivationRecord(source.Parent.Snapshot, report);
            all.Runs.Add(report.AgentRunId, report); all.Activations.Add(report.ActivationId, activation);
            all.ByAdmission.Add((report.SessionId, report.Admission.Activation.DedupeKey), report.AgentRunId);
            taskNotifications[(admission.TenantId, admission.TaskId)] = source.Notification with { Revision = source.Notification.Revision + 1, HandlingRunId = report.AgentRunId };
            BumpTaskSecurity(state); _audit.Add(new(admission.AuditId, admission.TenantId, agent.PrincipalId, "tasks.report", admission.TaskId.ToString("D"), "allowed", "parent_own_report_admitted", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.ReportAdmitted, source.Task);
            AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("report", source.Notification.CreatedAt, report.CreatedAtUtc);
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask RequireTaskOutcomeSourcesAsync(AgentRun worker, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var current = sessionBacking.ReadTenantRun(worker.Owner.TenantId!.Value, worker.AgentRunId);
            if (current is null || current.Owner != worker.Owner || current.Revision != worker.Revision || !current.IsTerminal
                || sessionBacking.ReadTenantSession(worker.Owner.TenantId.Value, worker.SessionId) is not { } session || SessionRetention.IsDue(session.Snapshot, TaskNow))
                throw new AccessDeniedException("task_result_source_unavailable");
            RequireTenantOutputSources(current, ct);
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask RequireTaskReportSourcesAsync(AgentRun report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var work = report.Admission.TaskReport ?? throw new AccessDeniedException("task_report_unavailable"); var source = TaskReportSource(report.Owner.TenantId!.Value, work.TaskId);
            if (source.Notification.NotificationId != work.NotificationId || source.Notification.HandlingRunId is null || source.Task.Revision != work.TaskRevision
                || source.Task.TerminalRunId != work.TerminalRunId || source.Parent?.Owner.SessionId != report.SessionId
                || source.Parent.Owner.ExecutorPrincipalId != report.Owner.ExecutorPrincipalId || source.Parent.Owner.AgentInstanceId != report.AgentInstanceId)
                throw new AccessDeniedException("task_report_unavailable");
            TaskReportRules.RequireHandlingRunAsync(source, report,
                (id, _) => ValueTask.FromResult(sessionBacking.ReadTenantRun(report.Owner.TenantId!.Value, id)), ct).AsTask().GetAwaiter().GetResult();
            RequireTaskReport(source, ct); return ValueTask.CompletedTask;
        }
    }
}
