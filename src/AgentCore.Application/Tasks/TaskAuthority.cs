using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class TaskAuthority
{
    // Used only by a stored, authenticated human command's approval owner. Public
    // reads still require the live HumanSessionReference at their application gate.
    public static async ValueTask<bool> CanReadHumanCommandAsync(TenantSecurityState state, Guid human, DelegatedTask task, IamAuthorizer iam, CancellationToken ct)
    {
        if (task.Source.TenantId != state.Tenant.TenantId || !state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) return false;
        foreach (var id in new[] { task.Source.InstanceId, task.Assignment.InstanceId }.Distinct())
            if (!state.SystemIdentities.Any(i => i.AgentInstanceId == id && i.Status == IdentityStatus.Active)
                || !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("agents.read"), AccessScope.ForResource(task.Source.TenantId, "agent", id.ToString("D")), ct)).Allowed) return false;
        return (await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("tasks.read"), Scope(task.Source.TenantId, task.TaskId), ct)).Allowed
            && (await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), ct)).Allowed;
    }
    public static string PreparationPermission(TaskActor actor, TaskPreparationPurpose purpose)
    {
        actor.Validate();
        return purpose switch {
            TaskPreparationPurpose.Direct => "tasks.delegate",
            TaskPreparationPurpose.Consent when actor.Agent is not null && actor.Tool == "tasks.delegate" => "sessions.read",
            _ => throw new AccessDeniedException("task_preparation_unavailable") };
    }
    public static bool TryScope(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty
            && scope.ResourceType is "task" or "agent-thread" && Guid.TryParseExact(scope.ResourceId, "D", out id)
            && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static AccessScope Scope(Guid tenant, Guid task) => AccessScope.ForResource(tenant, "task", task.ToString("D"));
    public static async ValueTask RequireAsync(TenantSecurityState state, Guid actor, string permission, AccessScope scope, IamAuthorizer iam, CancellationToken ct)
    {
        if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
    }
    public static async ValueTask RequireCurrentPolicyAsync(TenantSecurityState state, TaskActor actor, Guid? requester,
        string permission, AccessScope scope, IamAuthorizer iam, CancellationToken ct)
    {
        if (actor.Agent is not null && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false))
            throw new AccessDeniedException("task_review_required");
    }
    public static async ValueTask SourceAsync(TenantSecurityState state, TaskActor actor, TenantSession session, TenantAgent agent,
        AgentRun? run, AgentDefinition current, IamAuthorizer iam, DateTimeOffset at, string permission, bool mutate, CancellationToken ct, AccessScope? permissionScope = null, bool discover = false)
    {
        actor.Validate();
        if (session.Owner.TenantId != state.Tenant.TenantId || agent.TenantId != state.Tenant.TenantId || agent.Instance.InstanceId != session.Owner.AgentInstanceId
            || agent.PrincipalId != session.Owner.ExecutorPrincipalId || state.Tenant.Status != IdentityStatus.Active
            || SessionRetention.IsDue(session.Snapshot, at) || session.Snapshot.DurablyDeletedAt is not null)
            throw new AccessDeniedException("resource_unavailable");
        var scope = TenantSessionRules.Scope(state.Tenant.TenantId, session.Owner.SessionId);
        await RequireAsync(state, actor.PrincipalId, "sessions.read", scope, iam, ct);
        var operationScope = permissionScope ?? scope;
        if (discover)
        {
            var metadata = PermissionCatalog.Get(permission);
            var scopes = new[] { AccessScope.ForTenant(state.Tenant.TenantId), scope,
                AccessScope.ForResource(state.Tenant.TenantId, "agent", agent.Instance.InstanceId.ToString("D")) }
                .Concat(state.Assignments.Where(a => a.PrincipalId == actor.PrincipalId && a.Scope.Kind == AccessScopeKind.Resource
                    && metadata.AppliesToResource(a.Scope.ResourceType)).Select(a => a.Scope)).Distinct();
            var allowed = false;
            foreach (var candidate in scopes)
                if ((await iam.CheckTenantAsync(state, actor.PrincipalId, metadata, candidate, ct)).Allowed
                    && (actor.Agent is null || !await TenantSandboxReadOperationRules.NeedsReviewAsync(state,
                        run?.Admission.ExecutionContext?.OriginalInitiatorPrincipalId, permission, candidate, iam, ct)))
                { operationScope = candidate; allowed = true; break; }
            if (!allowed) throw new AccessDeniedException("resource_unavailable");
        }
        else await RequireAsync(state, actor.PrincipalId, permission, operationScope, iam, ct);
        if (mutate)
        {
            if (session.Snapshot.ArchivedAt is not null || SessionLifecycle.IsTerminal(session.Snapshot.LifecycleStatus)
                || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
                throw new AccessDeniedException("session_unavailable");
            TenantSessionRules.RequireExecutor(state, agent);
            if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        }
        if (actor.Agent is { } frame)
        {
            if (run is null || run.AgentInstanceId != agent.Instance.InstanceId) throw new AccessDeniedException("task_source_unavailable");
            if (actor.Tool == "tasks.delegate" && run.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
                throw new AccessDeniedException("task_report_delegation_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, at);
            if (actor.Tool != "tasks.delegate")
                await RequireCurrentPolicyAsync(state, actor, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId, permission, operationScope, iam, ct);
            if (!RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, actor.Tool!) || !RolePermissions.AllowsTool(current, actor.Tool!))
                throw new AccessDeniedException("task_tool_unavailable");
        }
        else if (mutate) await RequireAsync(state, actor.PrincipalId, "sessions.manage", scope, iam, ct);
    }
    public static async ValueTask TargetAsync(TenantSecurityState state, Guid sourcePrincipal, TenantAgent target, IamAuthorizer iam, CancellationToken ct)
    {
        if (target.TenantId != state.Tenant.TenantId || target.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || target.Instance.Cooperation?.AcceptTasks != true) throw new AccessDeniedException("task_target_unavailable");
        target.Instance.Cooperation.Validate(); TenantSessionRules.RequireExecutor(state, target);
        await RequireAsync(state, sourcePrincipal, "agents.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", target.Instance.InstanceId.ToString("D")), iam, ct);
        foreach (var permission in new[] { "sessions.create", "agent-runs.create" })
            await RequireAsync(state, target.PrincipalId, permission, AccessScope.ForTenant(state.Tenant.TenantId), iam, ct);
    }
    public static async ValueTask<bool> CanReadAsync(TenantSecurityState state, TaskActor actor, DelegatedTask task, IamAuthorizer iam, CancellationToken ct, AgentRun? reader = null)
    {
        actor.Validate();
        if (task.Source.TenantId != state.Tenant.TenantId) return false;
        if (new[] { task.Source.InstanceId, task.Assignment.InstanceId }.Distinct().Any(id =>
            !state.SystemIdentities.Any(i => i.TenantId == task.Source.TenantId && i.AgentInstanceId == id && i.Status == IdentityStatus.Active))) return false;
        if (actor.Agent is { } frame && (frame.Owner.TenantId != task.Source.TenantId
            || frame.Owner.AgentInstanceId != task.Source.InstanceId && frame.Owner.AgentInstanceId != task.Assignment.InstanceId)) return false;
        if (!(await iam.CheckOwnedTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("tasks.read"), Scope(task.Source.TenantId, task.TaskId), ct)).Allowed) return false;
        if (actor.Agent is { } actual)
        {
            if (reader?.AgentRunId != actual.RunId || reader.Owner != actual.Owner || reader.SessionId != actual.SessionId) return false;
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, reader.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                "tasks.read", Scope(task.Source.TenantId, task.TaskId), iam, ct)) return false;
        }
        // Humans need source Session scope and independently readable Agent identities.
        // Worker Session/transcript/result source rights are checked separately in projections.
        if (actor.Human is not null)
        {
            if (!(await iam.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), ct)).Allowed) return false;
            foreach (var id in new[] { task.Source.InstanceId, task.Assignment.InstanceId }.Distinct())
                if (!(await iam.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("agents.read"), AccessScope.ForResource(task.Source.TenantId, "agent", id.ToString("D")), ct)).Allowed) return false;
        }
        return true;
    }
    public static TaskSource Source(TenantSession session, TaskActor actor, AgentRun? run, DelegatedTask? parent, Guid root, Guid command)
    {
        actor.Validate();
        return actor.Human is not null
            ? new(session.Owner.TenantId, session.Owner.SessionId, session.Owner.AgentInstanceId, actor.PrincipalId, TaskActorKind.Human,
                null, null, command, actor.PrincipalId, root, null, 1)
            : new(session.Owner.TenantId, session.Owner.SessionId, session.Owner.AgentInstanceId, actor.PrincipalId, TaskActorKind.Agent,
                run!.AgentRunId, run.ActivationId, null, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                run.Admission.Activation.RootActivationId, parent?.TaskId, (parent?.Source.Depth ?? 0) + 1);
    }
    public static async ValueTask RequireDelegationReviewAsync(TenantSecurityState state, DelegatedTask task, IamAuthorizer iam, CancellationToken ct)
    {
        var mode = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("tasks.delegate")).Mode;
        foreach (var key in new[] { "sessions.create", "agent-runs.create" })
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, task.Source.OriginalInitiatorPrincipalId, key,
                AccessScope.ForTenant(task.Source.TenantId), iam, ct)) throw new AccessDeniedException("task_review_required");
        if (mode == OperationApprovalMode.Always || mode == OperationApprovalMode.WhenRequesterLacksPermission
            && task.Source.OriginalInitiatorPrincipalId is { } requester
            && !(await iam.CheckOwnedTenantAsync(state, requester, PermissionCatalog.Get("tasks.delegate"), TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId), ct)).Allowed)
            throw new AccessDeniedException("task_review_required");
    }
    public static AgentCore.Application.Sessions.AgentCoreException CapacityExceeded() => new("TaskCapacityExceeded", "The current Task capacity is full. Read saved tasks before trying again.", 429)
        { ValidationCode = "task_capacity_exceeded", RetryAfterMs = 5000 };
    public static void Bounds(DelegatedTask task, IReadOnlyList<DelegatedTask> all, DateTimeOffset at, AgentCooperationPolicy policy)
    {
        if (task.DeadlineAt <= at || task.DeadlineAt - task.CreatedAt > TaskLimits.MaxLifetime) throw new AccessDeniedException("task_deadline_invalid");
        if (task.Source.RunId is { } run && all.Count(t => t.Source.RunId == run) >= TaskLimits.MaxChildrenPerRun
            || all.Count(t => t.Source.RootActivationId == task.Source.RootActivationId && !TaskLimits.Terminal(t.State)) >= TaskLimits.MaxLiveDescendants
            || all.Count(t => t.Assignment.InstanceId == task.Assignment.InstanceId && !TaskLimits.Terminal(t.State)) >= policy.MaxConcurrentTasks)
            throw CapacityExceeded();
        var dependencies = task.Request.DependsOnTaskIds.Select(id => all.SingleOrDefault(t => t.TaskId == id)).ToArray();
        if (dependencies.Any(t => t is null || t.Source.TenantId != task.Source.TenantId || t.Source.SessionId != task.Source.SessionId
            || t.Source.ParentTaskId != task.Source.ParentTaskId || t.CreatedAt > task.CreatedAt)) throw new AccessDeniedException("task_dependency_unavailable");
        if (task.Source.ParentTaskId is { } parentId)
        {
            var parent = all.SingleOrDefault(t => t.TaskId == parentId) ?? throw new AccessDeniedException("task_parent_unavailable");
            if (TaskLimits.Terminal(parent.State) || parent.DeadlineAt < task.DeadlineAt || parent.WorkerSessionId != task.Source.SessionId
                || parent.Assignment.InstanceId != task.Source.InstanceId || parent.Source.RootActivationId != task.Source.RootActivationId)
                throw new AccessDeniedException("task_parent_unavailable");
        }
        if (task.Request.RetryOfTaskId is { } retry && !all.Any(t => t.TaskId == retry && TaskLimits.Terminal(t.State)
            && t.Source.SessionId == task.Source.SessionId && t.State != DelegatedTaskState.NeedsAttention)) throw new AccessDeniedException("task_retry_unavailable");
    }
}
