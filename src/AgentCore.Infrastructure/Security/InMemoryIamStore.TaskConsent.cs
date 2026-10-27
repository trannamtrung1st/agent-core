using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TaskConsentAuthority? TaskConsent(AgentCore.Domain.Tasks.DelegatedTask task, TenantSecurityState state, CancellationToken ct)
    {
        var request = _approvals.Values.SingleOrDefault(r => r.TenantId == task.Source.TenantId && r.Operation.TaskDelegation?.Task.TaskId == task.TaskId);
        if (request is not null && TenantTaskConsentRules.IsHuman(request))
            return TaskConsentAuthority.VerifyHumanAsync(task, request, _approvalDecisions.GetValueOrDefault(request.RequestId),
                humanTaskOutcomes.GetValueOrDefault((request.TenantId, request.Operation.OperationId)), state, new(this), ct).AsTask().GetAwaiter().GetResult();
        return request is null ? null : TaskConsentAuthority.VerifyAsync(task, request, _approvalDecisions.GetValueOrDefault(request.RequestId),
            _operationGrants.GetValueOrDefault(request.RequestId), _operationReceipts.GetValueOrDefault(request.Operation.OperationId), state, new(this), ct).AsTask().GetAwaiter().GetResult();
    }
    private (TenantSession Source, AgentRun Original, TenantAgent Worker) RequireTaskConsentOperation(TenantApprovalRequest request,
        AgentRun run, TenantAgent agent, CancellationToken ct, bool creation = false)
    {
        var p = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_review_unavailable"); p.Validate();
        var task = p.Task; var state = _tenants[request.TenantId];
        var source = sessionBacking.ReadTenantSession(request.TenantId, task.Source.SessionId) ?? throw new AccessDeniedException("task_source_unavailable");
        var original = task.Source.RunId is { } id ? sessionBacking.ReadTenantRun(request.TenantId, id) : null;
        if (original is null) throw new AccessDeniedException("task_source_unavailable");
        TenantTaskConsentRules.RequireModelSourceAsync(request, run, original, source, agent,
            InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), state, new(this), TaskNow, ct, creation).AsTask().GetAwaiter().GetResult();
        var worker = RequireTaskConsentWorker(task, state, ct);
        return (source, original, worker);
    }
    private TenantAgent RequireTaskConsentWorker(AgentCore.Domain.Tasks.DelegatedTask task, TenantSecurityState state, CancellationToken ct)
    {
        var worker = FindTenantAgentAsync(task.Source.TenantId, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
        TaskAuthority.TargetAsync(state, task.Source.PrincipalId, worker, new(this), ct).AsTask().GetAwaiter().GetResult();
        if (worker.Instance.Revision != task.Assignment.InstanceRevision || worker.PrincipalId != task.Assignment.PrincipalId || task.DeadlineAt <= TaskNow)
            throw new AccessDeniedException("task_assignment_changed");
        var space = FindSpaceAsync(task.Source.TenantId, task.Assignment.SpaceId, ct).Result;
        if (space is null || space.Kind == SpaceKind.Private && space.OwnerInstanceId != worker.Instance.InstanceId
            || !_spaceMemberships.ContainsKey((task.Source.TenantId, task.Assignment.SpaceId, worker.Instance.InstanceId)))
            throw new AccessDeniedException("space_membership_unavailable");
        if (task.Assignment.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != task.Assignment.EnvironmentSelectionEpoch
            || FindEnvironmentAsync(task.Source.TenantId, space.SpaceId, environment, ct).Result is null)) throw new AccessDeniedException("environment_unavailable");
        TenantSpaceContextResolver.RequireCurrentPermissionsAsync(state, worker.PrincipalId, space.SpaceId, task.Assignment.EnvironmentId is not null,
            new(this), ct).AsTask().GetAwaiter().GetResult();
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(task.Assignment.Model!, modelSettings.GetValueOrDefault(task.Source.TenantId)?.Revision ?? 0);
        return worker;
    }
    private TenantAgent ValidateApprovedTask(TenantApprovalRequest request, TaskAdmission admission, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        TenantTaskConsentRules.MatchAdmission(request, admission);
        var current = RequireTaskConsentOperation(request, run, agent, ct); var task = admission.Task; var state = _tenants[request.TenantId];
        if (admission.ExpectedSecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        if (delegatedTasks.ContainsKey((request.TenantId, task.TaskId)) || delegatedTasks.Values.Any(t => t.Source.TenantId == request.TenantId
            && t.Source.OperationScope == task.Source.OperationScope && t.Request.RequestKey == task.Request.RequestKey)) throw new SecurityConflictException("task_request_conflict");
        TaskAdmissionFactory.RequireGraph(admission, current.Source, current.Original, current.Worker, TaskNow);
        TaskAuthority.Bounds(task, delegatedTasks.Values.Where(t => t.Source.TenantId == request.TenantId).ToArray(), TaskNow, current.Worker.Instance.Cooperation!);
        RequireTaskBudget(admission, run, current.Worker, task.Source.RunId);
        var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), Tool: "tasks.delegate");
        foreach (var dependency in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
            if (!CanReadTask(state, actor, delegatedTasks[(request.TenantId, dependency)], ct))
                throw new AccessDeniedException("task_dependency_unavailable");
        if (admission.WorkerRun is { } worker)
            RequireSpaceExecutionSource(worker, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
        return current.Worker;
    }
    private void StageApprovedTask(TaskAdmission admission, TenantAgent worker)
    {
        var task = admission.Task;
        if (admission.WorkerRun is { } run)
            StageTaskGraph(new(admission.TenantId, run.SessionId, worker.Instance.InstanceId, worker.PrincipalId,
                task.Source.OriginalInitiatorPrincipalId ?? worker.PrincipalId, task.CreatedAt), admission.WorkerSession!, run);
        delegatedTasks.Add((admission.TenantId, task.TaskId), task);
    }
}
