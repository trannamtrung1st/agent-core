using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantTaskStore
{
    private readonly Dictionary<(Guid Tenant, Guid Task), DelegatedTask> delegatedTasks = [];
    private readonly Dictionary<(Guid Tenant, Guid Task, Guid Actor, string Key), (string Hash, DelegatedTask Task)> taskCommands = [];
    private readonly Dictionary<(Guid Tenant, Guid Task), TaskNotification> taskNotifications = [];
    private DateTimeOffset TaskNow => DateTimeOffset.FromUnixTimeMilliseconds((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
    public ValueTask<IReadOnlyDictionary<Guid, int>> ReadTaskCapacityAsync(Guid tenant, IReadOnlyList<Guid> candidates, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (candidates.Count is < 1 or > 100) throw new ArgumentException("Use a bounded candidate set.");
        lock (_gate) return ValueTask.FromResult<IReadOnlyDictionary<Guid, int>>(candidates.Distinct().ToDictionary(id => id,
            id => delegatedTasks.Values.Count(t => t.Source.TenantId == tenant && t.Assignment.InstanceId == id && !TaskLimits.Terminal(t.State))));
    }
    public ValueTask<DelegatedTask?> FindTaskAsync(Guid tenant, Guid task, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(delegatedTasks.GetValueOrDefault((tenant, task)) is { } value
            && (value.Source.Workflow is { } source ? workflowExecutions.ContainsKey((tenant, source.ExecutionId))
                : sessionBacking.ReadTenantSession(tenant, value.Source.RequireSessionId()) is not null) ? value : null);
    }
    private bool CanReadTask(TenantSecurityState state, TaskActor actor, DelegatedTask task, CancellationToken ct) =>
        (task.Source.Workflow is null || TaskSourceAvailable(task, ct, dispatch: false)) && TaskAuthority.CanReadAsync(state, actor, task, new(this), ct, actor.Agent is { } frame
            ? sessionBacking.ReadRetainedTenantOperationRun(state.Tenant.TenantId, frame.RunId) : null).Result;
    private void RequireTaskReader(Guid tenant, TaskActor actor, CancellationToken ct)
    {
        actor.Validate();
        if (actor.Human is { } human) RequireHumanSession(tenant, human);
        else
        {
            var frame = actor.Agent!;
            var run = sessionBacking.ReadTenantRun(tenant, frame.RunId) ?? throw new AccessDeniedException("resource_unavailable");
            var session = sessionBacking.ReadTenantSession(tenant, frame.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            if (frame.Owner.TenantId != tenant) throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            TaskAuthority.SourceAsync(_tenants[tenant], actor, session, agent, run, current, new(this), TaskNow,
                "sessions.read", false, ct).AsTask().GetAwaiter().GetResult();
        }
    }
    private (TenantSession Session, AgentRun? Run, TenantAgent Agent) RequireTaskSource(Guid tenant, Guid source, TaskActor actor, string permission, CancellationToken ct, AgentCore.Domain.Security.AccessScope? permissionScope = null)
    {
        actor.Validate(); if (actor.Human is { } human) RequireHumanSession(tenant, human);
        var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("tenant_unavailable");
        var session = sessionBacking.ReadTenantSession(tenant, source) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = FindTenantAgentAsync(tenant, session.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        var run = actor.Agent is { } frame ? sessionBacking.ReadTenantRun(tenant, frame.RunId) : null;
        TaskAuthority.SourceAsync(state, actor, session, agent, run,
            InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), new(this), TaskNow, permission, true, ct, permissionScope).AsTask().GetAwaiter().GetResult();
        return (session, run, agent);
    }
    public ValueTask<TaskMutationResult?> FindTaskRequestAsync(Guid tenant, Guid sourceSession, TaskActor actor, string requestKey, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireTaskSource(tenant, sourceSession, actor, TaskAuthority.PreparationPermission(actor, purpose), ct);
            var scope = actor.Agent is { } frame ? $"run:{frame.RunId:D}" : $"human:{actor.PrincipalId:D}:session:{sourceSession:D}";
            var saved = delegatedTasks.Values.SingleOrDefault(t => t.Source.TenantId == tenant && t.Source.SessionId == sourceSession
                && t.Source.OperationScope == scope && t.Request.RequestKey == requestKey);
            return ValueTask.FromResult<TaskMutationResult?>(saved is null ? null : new(saved, _tenants[tenant].Tenant.Revision, true));
        }
    }
    public ValueTask<TaskMutationResult> AdmitTaskAsync(TaskAdmission admission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var tenant = admission.TenantId; var (source, run, _) = RequireTaskSource(tenant, admission.SourceSessionId, admission.Actor, "tasks.delegate", ct);
            var state = _tenants[tenant]; var task = admission.Task;
            var previous = delegatedTasks.Values.SingleOrDefault(t => t.Source.TenantId == tenant && t.Source.SessionId == admission.SourceSessionId
                && t.Source.OperationScope == task.Source.OperationScope && t.Request.RequestKey == admission.Request.RequestKey);
            if (previous is not null)
            {
                if (previous.Request.Fingerprint() != admission.Request.Fingerprint()) throw new SecurityConflictException("task_request_conflict");
                return ValueTask.FromResult(new TaskMutationResult(previous, state.Tenant.Revision, true));
            }
            if (_approvals.Values.Any(r => r.TenantId == tenant && r.Operation.TaskDelegation is { } binding
                && binding.Task.Source.OperationScope == task.Source.OperationScope && binding.Task.Request.RequestKey == task.Request.RequestKey))
                throw new SecurityConflictException("task_approval_request_exists");
            if (state.Tenant.Revision != admission.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
            var target = FindTenantAgentAsync(tenant, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
            TaskAuthority.TargetAsync(state, admission.Actor.PrincipalId, target, new(this), ct).AsTask().GetAwaiter().GetResult();
            TaskAdmissionFactory.RequireGraph(admission, source, run, target, TaskNow);
            TaskAuthority.Bounds(task, delegatedTasks.Values.Where(t => t.Source.TenantId == tenant).ToArray(), TaskNow, target.Instance.Cooperation!);
            RequireTaskReview(state, admission.Actor, task, ct);
            RequireTaskBudget(admission, run, target);
            foreach (var dependency in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
                if (!CanReadTask(state, admission.Actor, delegatedTasks[(tenant, dependency)], ct))
                    throw new AccessDeniedException("task_dependency_unavailable");
            if (delegatedTasks.ContainsKey((tenant, task.TaskId)) || _audit.Any(a => a.AuditId == admission.AuditId)) throw new SecurityConflictException("identity_conflict");
            if (admission.WorkerRun is { } worker)
            {
                RequireSpaceExecutionSource(worker, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
                AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(worker.PinnedModel, modelSettings.GetValueOrDefault(tenant)?.Revision ?? 0);
                StageTaskGraph(new(tenant, worker.SessionId, worker.AgentInstanceId, target.PrincipalId, task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, task.CreatedAt), admission.WorkerSession!, worker);
            }
            delegatedTasks.Add((tenant, task.TaskId), task); BumpTaskSecurity(state);
            _audit.Add(new(admission.AuditId, tenant, admission.Actor.PrincipalId, "tasks.delegate", task.TaskId.ToString("D"), "allowed", "worker_own_admission", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Accepted, task);
            return ValueTask.FromResult(new TaskMutationResult(task, state.Tenant.Revision + 1));
        }
    }
    private void RequireTaskReview(TenantSecurityState state, TaskActor actor, DelegatedTask task, CancellationToken ct) =>
        TaskAuthority.RequireDelegationReviewAsync(state, task, new(this), ct).AsTask().GetAwaiter().GetResult();
    private void BumpTaskSecurity(TenantSecurityState state) => _tenants[state.Tenant.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private void StageTaskGraph(TenantSessionRoot root, SessionSnapshot snapshot, AgentRun run)
    {
        var all = sessionBacking.AgentRuns;
        if (sessionBacking.HasTenantBackgroundIdentityConflict(snapshot) || all.Runs.ContainsKey(run.AgentRunId) || all.Activations.ContainsKey(run.ActivationId)
            || all.ByAdmission.ContainsKey((run.SessionId, run.Admission.Activation.DedupeKey)) || sessionBacking.FindEntry(run.SessionId, snapshot.Entries.Single().EntryId) is not null)
            throw new SecurityConflictException("identity_conflict");
        if (!sessionBacking.CreateTenantSession(root, snapshot)) throw new SecurityConflictException("identity_conflict");
        all.Runs.Add(run.AgentRunId, run); all.Activations.Add(run.ActivationId, AgentRunStoreMapping.ToActivationRecord(snapshot, run));
        all.ByAdmission.Add((run.SessionId, run.Admission.Activation.DedupeKey), run.AgentRunId);
        all.BySourceEntry.Add((run.SessionId, snapshot.Entries.Single().EntryId), run.ActivationId);
    }
    public ValueTask<TaskPage<DelegatedTask>> ReadTasksAsync(TaskQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (query.Limit is < 1 or > TaskLimits.MaxPage || query.Scope is not ("created" or "assigned" or "pending")) throw new ArgumentException("Invalid task page.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireTaskReader(query.TenantId, query.Actor, ct); var state = _tenants[query.TenantId];
            var result = new List<DelegatedTask>();
            foreach (var task in delegatedTasks.Values.Where(t => t.Source.TenantId == query.TenantId).OrderBy(t => t.TaskId))
            {
                if (query.Cursor is { } cursor && task.TaskId.CompareTo(cursor) <= 0 || query.SessionId is { } session && (query.Scope == "assigned" ? task.WorkerSessionId != session : task.Source.SessionId != session)
                    || query.InstanceId is { } instance && (query.Scope == "created" ? task.Source.InstanceId != instance : task.Assignment.InstanceId != instance)
                    || query.Scope == "pending" && TaskLimits.Terminal(task.State)
                    || !TaskSourceAvailable(task, ct, dispatch: false)
                    || !CanReadTask(state, query.Actor, task, ct)) continue;
                result.Add(task); if (result.Count > query.Limit) break;
            }
            return ValueTask.FromResult(new TaskPage<DelegatedTask>(result.Take(query.Limit).ToArray(), result.Count > query.Limit ? result[query.Limit - 1].TaskId : null,
                result.Count > query.Limit, state.Tenant.Revision));
        }
    }
    public ValueTask<TaskMutationResult> CancelTaskAsync(TaskCancel command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var tenant = command.TenantId; RequireTaskReader(tenant, command.Actor, ct); var state = _tenants[tenant];
            var task = delegatedTasks.GetValueOrDefault((tenant, command.TaskId)) ?? throw new AccessDeniedException("resource_unavailable");
            if (!CanReadTask(state, command.Actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
            if (command.Actor.Human is not null) TaskAuthority.RequireAsync(state, command.Actor.PrincipalId, task.Source.Workflow is null ? "sessions.manage" : "workflows.cancel",
                task.Source.Workflow is { } process ? AgentCore.Application.Workflows.WorkflowCatalogAuthority.Scope(tenant, process.WorkflowId) : TenantSessionRules.Scope(tenant, task.Source.RequireSessionId()), new(this), ct).AsTask().GetAwaiter().GetResult();
            TaskAuthority.RequireAsync(state, command.Actor.PrincipalId, "tasks.cancel", TaskAuthority.Scope(tenant, task.TaskId), new(this), ct).AsTask().GetAwaiter().GetResult();
            var requester = command.Actor.Agent is { } frame ? sessionBacking.ReadTenantRun(tenant, frame.RunId)?.Admission.ExecutionContext?.OriginalInitiatorPrincipalId : null;
            TaskAuthority.RequireCurrentPolicyAsync(state, command.Actor, requester, "tasks.cancel", TaskAuthority.Scope(tenant, task.TaskId), new(this), ct).AsTask().GetAwaiter().GetResult();
            var hash = TaskLimits.Hash(new { command.TaskId, command.ExpectedRevision, command.Reason }); var key = (tenant, task.TaskId, command.Actor.PrincipalId, TaskLimits.Key(command.RequestKey));
            if (taskCommands.TryGetValue(key, out var previous))
            {
                if (previous.Hash != hash) throw new SecurityConflictException("task_request_conflict");
                return ValueTask.FromResult(new TaskMutationResult(previous.Task, state.Tenant.Revision, true));
            }
            if (state.Tenant.Revision != command.ExpectedSecurityRevision || task.Revision != command.ExpectedRevision || TaskLimits.Terminal(task.State)) throw new SecurityConflictException("stale_revision");
            var at = TaskNow; var next = task.Advance(task.State == DelegatedTaskState.PendingDependencies ? DelegatedTaskState.Blocked : task.State, at,
                reason: task.State == DelegatedTaskState.PendingDependencies ? "cancelled_before_dependencies" : "cancellation_requested", cancellationRequested: true);
            if (task.WorkerSessionId is not null)
            {
                var worker = TaskSettlement.FinalRun(task, id => sessionBacking.ReadRetainedTenantOperationRun(tenant, id));
                if (!worker.IsTerminal) sessionBacking.AgentRuns.Runs[worker.AgentRunId] = worker.RequestCancellation(worker.Revision, worker.KnownEffectSummary, at);
            }
            delegatedTasks[(tenant, task.TaskId)] = next; taskCommands.Add(key, (hash, next)); BumpTaskSecurity(state);
            if (TaskLimits.Terminal(next.State) && task.Source.Workflow is null)
                taskNotifications.TryAdd((tenant, task.TaskId), new(tenant, command.AuditId, task.TaskId, next.TerminalRunId, task.Source.RequireSessionId(), 1, at));
            _audit.Add(new(command.AuditId, tenant, command.Actor.PrincipalId, "tasks.cancel", task.TaskId.ToString("D"), "allowed", "cancellation_requested", at));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.CancellationRequested, next);
            return ValueTask.FromResult(new TaskMutationResult(next, state.Tenant.Revision + 1));
        }
    }
    private int taskReconciliationOffset;
    public ValueTask<IReadOnlyList<TaskReconciliation>> ListTaskReconciliationAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var candidates = delegatedTasks.Values.Where(t => !TaskLimits.Terminal(t.State)).OrderBy(t => t.TaskId).ToArray();
            var offset = candidates.Length == 0 ? 0 : taskReconciliationOffset % candidates.Length;
            taskReconciliationOffset = checked(offset + Math.Clamp(limit, 1, 32));
            return ValueTask.FromResult<IReadOnlyList<TaskReconciliation>>(candidates.Skip(offset).Concat(candidates.Take(offset)).Take(Math.Clamp(limit, 1, 32)).Select(t => new TaskReconciliation(t, t.Source.SessionId is { } sourceId ? sessionBacking.ReadTenantSession(t.Source.TenantId, sourceId) : null,
                    t.InitialWorkerRunId is null ? null : TaskSettlement.FinalRun(t, id => sessionBacking.ReadRetainedTenantOperationRun(t.Source.TenantId, id)),
                    t.Request.DependsOnTaskIds.Select(id => delegatedTasks[(t.Source.TenantId, id)]).ToArray())).ToArray());
        }
    }
    public ValueTask<bool> ReconcileTaskAsync(Guid tenant, Guid taskId, long expectedRevision, Guid auditId, CancellationToken ct = default, string? readinessRefusal = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var task = delegatedTasks.GetValueOrDefault((tenant, taskId));
            if (task is null || task.Revision != expectedRevision || TaskLimits.Terminal(task.State)) return ValueTask.FromResult(false);
            var at = TaskNow; DelegatedTask next;
            if (task.InitialWorkerRunId is null)
            {
                if (readinessRefusal is not null || task.DeadlineAt <= at || task.Request.DependsOnTaskIds.Any(id => TaskLimits.Terminal(delegatedTasks[(tenant, id)].State) && delegatedTasks[(tenant, id)].State != DelegatedTaskState.Succeeded))
                    next = task.Advance(DelegatedTaskState.Blocked, at, reason: readinessRefusal ?? (task.DeadlineAt <= at ? "task_deadline_exceeded" : "dependency_failed"));
                else return ValueTask.FromResult(false);
            }
            else
            {
                var run = TaskSettlement.FinalRun(task, id => sessionBacking.ReadRetainedTenantOperationRun(tenant, id));
                var target = FindTenantAgentAsync(tenant, task.Assignment.InstanceId, ct).Result;
                var stop = TaskSettlement.StopReason(task, run, TaskSourceAvailable(task, ct),
                    target?.Instance.Lifecycle == AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active && target.Instance.Cooperation?.AcceptTasks == true, at);
                if (stop is not null && !run.IsTerminal)
                {
                    if (!run.CancellationRequested) { run = TenantRunCancellationRules.Cancel(run, at); sessionBacking.AgentRuns.Runs[run.AgentRunId] = run; }
                }
                next = TaskSettlement.Observe(task, run, at, stop); if (ReferenceEquals(task, next)) return ValueTask.FromResult(false);
            }
            delegatedTasks[(tenant, taskId)] = next;
            if (TaskLimits.Terminal(next.State) && task.Source.SessionId is not null)
                taskNotifications.TryAdd((tenant, taskId), new(tenant, auditId, taskId, next.TerminalRunId, task.Source.RequireSessionId(), 1, at));
            _audit.Add(new(auditId, tenant, task.Assignment.PrincipalId, "tasks.settle", taskId.ToString("D"), "allowed", next.State.ToString(), at));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Settled, next, next.Reason);
            return ValueTask.FromResult(true);
        }
    }
}
