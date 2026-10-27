using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public sealed record TaskView(Guid TaskId, long Revision, string State, string Title, string Objective,
    Guid? SourceSessionId, Guid? SelectedTargetInstanceId, string? SelectedTargetName, Guid? SpaceId, Guid? WorkerSessionId, Guid? InitialRunId, Guid? TerminalRunId,
    string? Result, string? Reason, bool CancellationRequested, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset DeadlineAt,
    bool ReportOnCompletion, bool CanInspect, bool CanCancel, bool CanSend, long SecurityRevision, Guid? RetryOfTaskId, bool CanRetry,
    string SourceActorKind, Guid? SourceInstanceId, string? SourceAgentName, Guid? SourceRunId, bool CanAcknowledge)
{
    public string? SpaceName { get; init; }
    public string? EnvironmentName { get; init; }
    public bool? EnvironmentConfigured { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TaskNoticeView>? Notifications { get; init; }
}
public sealed record TaskNoticeView(Guid NotificationId, Guid TaskId, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt, string? WithheldReason, bool ReportAdmitted);

public static class TaskProjection
{
    public static async ValueTask<TaskView> ForAsync(TenantSecurityState state, TaskActor actor, DelegatedTask task, IamAuthorizer iam, ITenantSessionStore sessions, DateTimeOffset at, CancellationToken ct, TenantRunSourceAuthority? sources = null)
    {
        AgentCore.Domain.Conversation.AgentRun? reader = null;
        var requester = actor.Human?.PrincipalId;
        if (actor.Agent is { } frame)
        {
            reader = sessions is ITenantRunStore runs ? await runs.FindTenantRunAsync(state.Tenant.TenantId, frame.RunId, ct) : null;
            if (reader?.Owner != frame.Owner || reader.SessionId != frame.SessionId || reader.Claim?.Generation != frame.Generation)
                throw new AccessDeniedException("resource_unavailable");
            requester = reader.Admission.ExecutionContext?.OriginalInitiatorPrincipalId;
        }
        if (!await TaskAuthority.CanReadAsync(state, actor, task, iam, ct, reader)) throw new AccessDeniedException("resource_unavailable");
        async ValueTask<bool> Read(string permission, string type, Guid id) => (await iam.CheckOwnedTenantAsync(state, actor.PrincipalId,
            PermissionCatalog.Get(permission), AccessScope.ForResource(state.Tenant.TenantId, type, id.ToString("D")), ct)).Allowed;
        async ValueTask<bool> SourceRead(string permission, string type, Guid id) => await Read(permission, type, id)
            && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission,
                AccessScope.ForResource(state.Tenant.TenantId, type, id.ToString("D")), iam, ct);
        if (await sessions.FindTenantSessionAsync(state.Tenant.TenantId, task.Source.SessionId, ct) is not { } sourceSession
            || AgentCore.Domain.Conversation.SessionRetention.IsDue(sourceSession.Snapshot, at)) throw new AccessDeniedException("resource_unavailable");
        var target = await SourceRead("agents.read", "agent", task.Assignment.InstanceId);
        var source = await SourceRead("sessions.read", "session", task.Source.SessionId);
        var sourceAgentReadable = await SourceRead("agents.read", "agent", task.Source.InstanceId);
        var sourceAgent = sourceAgentReadable && sessions is ITenantAgentStore agents
            ? await agents.FindTenantAgentAsync(state.Tenant.TenantId, task.Source.InstanceId, ct) : null;
        var sourceRunReadable = source && task.Source.RunId is { } sourceRunId && await SourceRead("agent-runs.read", "agent-run", sourceRunId);
        var worker = task.WorkerSessionId is { } session && await sessions.FindTenantSessionAsync(state.Tenant.TenantId, session, ct) is { } workerSession
            && !AgentCore.Domain.Conversation.SessionRetention.IsDue(workerSession.Snapshot, at) && await SourceRead("sessions.read", "session", session);
        var initial = worker && task.InitialWorkerRunId is { } run && await SourceRead("agent-runs.read", "agent-run", run);
        var outcome = worker && task.TerminalRunId is { } terminal && await SourceRead("agent-runs.read", "agent-run", terminal);
        if (outcome)
        {
            try
            {
                if (sessions is not ITenantRunStore outcomeRuns || sessions is not ITenantTaskStore outcomeTasks
                    || await outcomeRuns.FindTenantRunAsync(state.Tenant.TenantId, task.TerminalRunId!.Value, ct) is not { } final)
                    outcome = false;
                else
                {
                    await outcomeTasks.RequireTaskOutcomeSourcesAsync(final, ct);
                    if (TenantRunSourceAuthority.HasSources(final))
                        await (sources ?? throw new AccessDeniedException("source_context_unavailable")).RequireAsync(final, ct);
                }
            }
            catch (AccessDeniedException) { outcome = false; }
        }
        var mayManage = actor.Human is null || await Read("sessions.manage", "session", task.Source.SessionId);
        Guid? retryOf = null;
        if (task.Request.RetryOfTaskId is { } prior && sessions is ITenantTaskStore retryTasks
            && await retryTasks.FindTaskAsync(state.Tenant.TenantId, prior, ct) is { } previous
            && await TaskAuthority.CanReadAsync(state, actor, previous, iam, ct, reader)) retryOf = prior;
        var canRetry = actor.Human is not null && mayManage && source
            && task.State is DelegatedTaskState.Blocked or DelegatedTaskState.Failed or DelegatedTaskState.Cancelled
            && sourceSession.Snapshot.ArchivedAt is null && sourceSession.Snapshot.LifecycleStatus == AgentCore.Domain.Conversation.SessionLifecycleStatus.Active
            && sourceSession.Snapshot.Status is not (AgentCore.Domain.Conversation.SessionStatus.Ended or AgentCore.Domain.Conversation.SessionStatus.Ending)
            && await Read("tasks.delegate", "session", task.Source.SessionId);
        var spaceReadable = await SourceRead("spaces.read", "space", task.Assignment.SpaceId);
        var environmentReadable = spaceReadable && await SourceRead("environments.read", "space", task.Assignment.SpaceId);
        var savedSpace = spaceReadable && sessions is ITenantSpaceStore spaceStore
            ? await spaceStore.FindSpaceAsync(state.Tenant.TenantId, task.Assignment.SpaceId, ct) : null;
        var savedEnvironment = environmentReadable && task.Assignment.EnvironmentId is { } environmentId && sessions is ITenantEnvironmentStore environmentStore
            ? await environmentStore.FindEnvironmentAsync(state.Tenant.TenantId, task.Assignment.SpaceId, environmentId, ct) : null;
        return new(task.TaskId, task.Revision, task.State.ToString(), task.Request.Title, task.Request.Objective,
            source ? task.Source.SessionId : null, target ? task.Assignment.InstanceId : null, target ? task.Assignment.DisplayName : null,
            spaceReadable ? task.Assignment.SpaceId : null,
            worker ? task.WorkerSessionId : null, initial ? task.InitialWorkerRunId : null, outcome ? task.TerminalRunId : null,
            outcome ? task.Result : null, task.Reason, task.CancellationRequested, task.CreatedAt, task.UpdatedAt, task.DeadlineAt,
            task.Request.ReportOnCompletion, true,
            mayManage && !TaskLimits.Terminal(task.State) && await Read("tasks.cancel", "task", task.TaskId),
            mayManage && !TaskLimits.Terminal(task.State) && task.WorkerSessionId is not null && await Read("agent-messages.send", "task", task.TaskId), state.Tenant.Revision, retryOf, canRetry,
            task.Source.ActorKind.ToString(), sourceAgentReadable ? task.Source.InstanceId : null, sourceAgent?.Instance.Persona.Name,
            sourceRunReadable ? task.Source.RunId : null, mayManage && (actor.Agent is null || actor.Agent.SessionId == task.Source.SessionId)) {
                SpaceName = savedSpace is { DeletedAt: null } ? savedSpace.Name : null,
                EnvironmentName = savedEnvironment is { DeletedAt: null } ? savedEnvironment.Name : null,
                EnvironmentConfigured = environmentReadable ? task.Assignment.EnvironmentId is not null : null
            };
    }
}
