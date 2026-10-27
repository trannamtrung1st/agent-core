using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tasks;

public sealed record TaskReportSource(DelegatedTask Task, TaskNotification Notification, TenantSession? Parent, AgentRun? Worker);
public sealed record TaskReportAdmission(Guid TenantId, Guid TaskId, long ExpectedNoticeRevision, long ExpectedParentRevision,
    AgentRun Report, Guid AuditId);

/// <summary>The worker is a read-authorized source; the receiving Session always executes as its own Agent.</summary>
public static class TaskReportRules
{
    public static void Parent(TaskReportSource source, DateTimeOffset at)
    {
        var task = source.Task; var parent = source.Parent;
        if (task.TerminalRunId is null || source.Worker is null)
            throw new AccessDeniedException("task_report_no_worker_run");
        if (task.DeadlineAt <= at) throw new AccessDeniedException("task_report_deadline_exceeded");
        if (!task.Request.ReportOnCompletion || !TaskLimits.Terminal(task.State) || task.TerminalRunId != source.Worker?.AgentRunId
            || source.Notification.TaskId != task.TaskId || source.Notification.TerminalRunId != task.TerminalRunId
            || source.Notification.RecipientSessionId != task.Source.SessionId || source.Notification.TenantId != task.Source.TenantId
            || parent is null || parent.Owner.SessionId != task.Source.SessionId || parent.Owner.AgentInstanceId != task.Source.InstanceId
            || parent.Owner.TenantId != task.Source.TenantId || parent.Snapshot.DurablyDeletedAt is not null || parent.Snapshot.ArchivedAt is not null
            || SessionRetention.IsDue(parent.Snapshot, at) || parent.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || parent.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended or SessionStatus.Paused)
            throw new AccessDeniedException("task_report_parent_unavailable");
    }
    public static async ValueTask AuthorizeAsync(TaskReportSource source, TenantSecurityState state, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct)
    {
        Parent(source, at); var task = source.Task; var parent = source.Parent!; var principal = parent.Owner.ExecutorPrincipalId;
        await TaskAuthority.RequireAsync(state, principal, "tasks.read", TaskAuthority.Scope(task.Source.TenantId, task.TaskId), iam, ct);
        foreach (var instance in new[] { task.Source.InstanceId, task.Assignment.InstanceId }.Distinct())
            await TaskAuthority.RequireAsync(state, principal, "agents.read", AccessScope.ForResource(task.Source.TenantId, "agent", instance.ToString("D")), iam, ct);
        foreach (var (key, scope) in new[] {
            ("sessions.read", TenantSessionRules.Scope(task.Source.TenantId, task.WorkerSessionId!.Value)),
            ("agent-runs.read", TenantRunRules.Scope(task.Source.TenantId, task.TerminalRunId!.Value)),
            ("sessions.read", TenantSessionRules.Scope(task.Source.TenantId, parent.Owner.SessionId)),
            ("agent-runs.create", AccessScope.ForTenant(task.Source.TenantId)) })
        {
            await TaskAuthority.RequireAsync(state, principal, key, scope, iam, ct);
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, task.Source.OriginalInitiatorPrincipalId, key, scope, iam, ct))
                throw new AccessDeniedException("task_report_review_required");
        }
        if (task.Source.OriginalInitiatorPrincipalId is { } requester)
            foreach (var key in new[] { "sessions.read" })
                await TaskAuthority.RequireAsync(state, requester, key, TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), iam, ct);
    }
    public static AgentRun Create(TaskReportSource source, ResolvedAgentRunConfiguration resolved, AgentRunModelPin model,
        Guid activationId, Guid runId, Guid responseId, DateTimeOffset at)
    {
        Parent(source, at); var task = source.Task; var parent = source.Parent!; var worker = source.Worker!;
        var evidence = JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(TriggerKind.BackgroundCompleted,
            JsonSerializer.Serialize(new { taskId = task.TaskId, state = task.State.ToString(), summary = task.Result ?? task.Reason ?? "Worker ended.", reason = task.Reason,
                taskSuccessDoesNotVerifyGoal = true }), "task-completion", parent.Snapshot.DurableLastEntrySequence));
        var activation = new Activation(activationId, parent.Owner.SessionId, ActivationKind.TaskCompletion, [], source.Notification.NotificationId,
            null, worker.SessionId, worker.AgentRunId, $"task-completion:{source.Notification.NotificationId:D}", at,
            task.Source.RootActivationId, worker.ActivationId, evidence);
        var execution = new TrustedExecutionContext(task.Source.TenantId, parent.Owner.ExecutorPrincipalId, task.Source.OriginalInitiatorPrincipalId,
            parent.Owner.SessionId, runId, activationId, task.Source.RootActivationId, worker.AgentRunId, worker.ActivationId, ExecutionOrigin.TaskCompletion);
        return AgentRun.Create(runId, AgentRunOwner.ForTenant(task.Source.TenantId, parent.Owner.AgentInstanceId, parent.Owner.ExecutorPrincipalId),
            new(activation, resolved.Configuration.Definition.Id, resolved.Configuration.Definition.Version, resolved.Persona, responseId,
                AgentRunOutputContract.CompletionReport, TaskBudgetRules.Report(resolved.Configuration.Definition, resolved.InstanceBudgets,
                    task.Assignment.Budget ?? throw new AccessDeniedException("task_report_budget_unavailable")),
                resolved.Configuration, executionContext: execution, taskReport: new(task.TaskId, task.Revision, source.Notification.NotificationId, worker.AgentRunId)),
            model, AgentRunLimits.DefaultMaxAttempts, at, resolved.Skills, resolved.Skills.Where(s => s.Projection == AgentCore.Domain.Definitions.SkillProjection.Always).Select(s => s.Key).ToArray());
    }
    public static void Shape(TaskReportSource source, AgentRun report, TenantAgent parentAgent, long parentRevision, DateTimeOffset at)
    {
        Parent(source, at);
        if (source.Parent!.Snapshot.Revision != parentRevision || source.Parent.Owner.AgentInstanceId != parentAgent.Instance.InstanceId
            || source.Parent.Owner.ExecutorPrincipalId != parentAgent.PrincipalId || !report.IsInitialQueued || report.Admission.Configuration is not { } configuration
            || configuration.InstanceRevision != parentAgent.Instance.Revision || configuration.PersonaRevision != parentAgent.Instance.PersonaRevision
            || report.PinnedPersona != parentAgent.Instance.Persona || configuration.SpaceContext is not { } space
            || !space.MatchesSelection(source.Parent.Snapshot.ContextSelection))
            throw new SecurityConflictException("task_report_changed");
        var expected = Create(source, new(report.Admission.Configuration, parentAgent.Instance.Persona, report.PinnedSkillCatalog, parentAgent.Instance.ExecutionBudgets),
            report.PinnedModel, report.ActivationId, report.AgentRunId, report.ResponseId!.Value, report.CreatedAtUtc);
        if (report.Owner != expected.Owner || JsonSerializer.Serialize(report.Admission) != JsonSerializer.Serialize(expected.Admission))
            throw new AccessDeniedException("task_report_mismatch");
    }
    public static async ValueTask RequireHandlingRunAsync(TaskReportSource source, AgentRun report,
        Func<Guid, CancellationToken, ValueTask<AgentRun?>> findRun, CancellationToken ct)
    {
        var expected = source.Notification.HandlingRunId ?? throw new AccessDeniedException("task_report_unavailable");
        var current = report; var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64 && seen.Add(current.AgentRunId); depth++)
        {
            if (current.Owner != report.Owner || current.SessionId != report.SessionId || current.Admission.TaskReport != report.Admission.TaskReport
                || current.Admission.OutputContract != AgentRunOutputContract.CompletionReport) break;
            if (current.AgentRunId == expected)
            {
                if (current.Admission.ExecutionContext?.Origin == ExecutionOrigin.TaskCompletion
                    && current.Admission.Activation.Kind == ActivationKind.TaskCompletion
                    && current.Admission.Activation.SourceEventId == source.Notification.NotificationId) return;
                break;
            }
            if (current.Admission.ExecutionContext?.Origin != ExecutionOrigin.ApprovalContinuation
                || current.Admission.ApprovalResume?.SourceRunId is not { } previousId
                || await findRun(previousId, ct) is not { } previous
                || previous.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
                || previous.Result.ContinuationRunId != current.AgentRunId) break;
            current = previous;
        }
        throw new AccessDeniedException("task_report_unavailable");
    }

}
