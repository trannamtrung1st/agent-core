using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class TaskReadyRules
{
    public static async ValueTask RequireSourceAsync(DelegatedTask task, TenantSession source, AgentRun? sourceRun, TenantAgent agent,
        AgentDefinition current, TenantSecurityState state, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct, TaskConsentAuthority? consent = null)
    {
        if (source.Owner.TenantId != task.Source.TenantId || source.Owner.SessionId != task.Source.SessionId
            || source.Owner.AgentInstanceId != task.Source.InstanceId || source.Snapshot.DurablyDeletedAt is not null
            || SessionRetention.IsDue(source.Snapshot, at) || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active)
            throw new AccessDeniedException("task_source_unavailable");
        if (task.Source.ActorKind == TaskActorKind.Agent)
        {
            if (sourceRun is null || sourceRun.SessionId != source.Owner.SessionId || sourceRun.Owner.ExecutorPrincipalId != task.Source.PrincipalId
                || sourceRun.AgentRunId != task.Source.RunId || sourceRun.ActivationId != task.Source.ActivationId
                || !RolePermissions.AllowsTool(sourceRun.Admission.Configuration!.Definition, "tasks.delegate") || !RolePermissions.AllowsTool(current, "tasks.delegate"))
                throw new AccessDeniedException("task_source_unavailable");
        }
        if (consent is not null && consent.TaskId != task.TaskId) throw new AccessDeniedException("task_consent_unavailable");
        foreach (var key in task.Source.ActorKind == TaskActorKind.Human ? new[] { "sessions.read", "sessions.manage", "tasks.delegate" } : new[] { "sessions.read", "tasks.delegate" })
            if (key != "tasks.delegate" || consent?.Exceptional != true) await TaskAuthority.RequireAsync(state, task.Source.PrincipalId, key, TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), iam, ct);
        if (task.Source.OriginalInitiatorPrincipalId is { } requester && requester != task.Source.PrincipalId)
            await TaskAuthority.RequireAsync(state, requester, "sessions.read", TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), iam, ct);
        if (consent is null) await TaskAuthority.RequireDelegationReviewAsync(state, task, iam, ct);
    }
    public static DelegatedTask Graph(ReadyTaskAdmission admission, DelegatedTask task, IReadOnlyList<DelegatedTask> dependencies, TenantAgent target, DateTimeOffset at)
    {
        if (task.Source.TenantId != admission.TenantId || task.TaskId != admission.TaskId || task.State != DelegatedTaskState.PendingDependencies
            || task.Revision != admission.ExpectedRevision || task.DeadlineAt <= at || dependencies.Count != task.Request.DependsOnTaskIds.Count
            || dependencies.Any(t => !task.Request.DependsOnTaskIds.Contains(t.TaskId) || t.State != DelegatedTaskState.Succeeded || t.TerminalRunId is null))
            throw new SecurityConflictException("task_not_ready");
        var run = admission.Run; var session = admission.Session;
        var next = task.Advance(DelegatedTaskState.Queued, run.CreatedAtUtc, session.SessionId, run.AgentRunId);
        if (!run.IsInitialQueued || run.CreatedAtUtc > at || run.CreatedAtUtc < task.CreatedAt || target.Instance.Revision != task.Assignment.InstanceRevision)
            throw new AccessDeniedException("task_assignment_changed");
        var expected = TaskAdmissionFactory.Worker(next, target,
            new(run.Admission.Configuration!, target.Instance.Persona, run.PinnedSkillCatalog, target.Instance.ExecutionBudgets),
            task.Assignment.Model!, session.SessionId, session.Entries.Single().EntryId, run.ActivationId, run.AgentRunId, run.ResponseId!.Value, run.CreatedAtUtc);
        if (JsonSerializer.Serialize(expected.Run.Admission) != JsonSerializer.Serialize(run.Admission)
            || JsonSerializer.Serialize(expected.Session) != JsonSerializer.Serialize(session)) throw new AccessDeniedException("task_assignment_changed");
        return next;
    }
}
