using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

/// <summary>Authenticated human reads and commands. Direct Agent threads have no human posting method.</summary>
public sealed class TenantTaskAccessService(IIamStore security, ITenantTaskStore tasks, ITenantAgentMessageStore messages,
    ITenantSessionStore sessions, ITenantSpaceStore spaces, TenantAgentService agents, TenantTaskService service,
    IamAuthorizer iam, IIdGenerator ids, TimeProvider time, TenantRunSourceAuthority? sources = null, TenantApprovalService? approvals = null,
    TenantRunConfigurationResolver? configurations = null, ITenantModelCatalogReader? models = null)
{
    private async ValueTask<TenantSecurityState> State(Guid tenant, CancellationToken ct) => await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
    private async ValueTask Fence(Guid tenant, long revision, CancellationToken ct)
    { if ((await State(tenant, ct)).Tenant.Revision != revision) throw new SecurityConflictException("stale_revision"); }
    public async ValueTask<TaskPage<TaskView>> ListAsync(Guid tenant, HumanSessionReference human, Guid? session, Guid? instance, string scope, Guid? cursor, int limit, CancellationToken ct)
    {
        var actor = new TaskActor(Human: human); var page = await tasks.ReadTasksAsync(new(tenant, actor, session, instance, scope, cursor, limit), ct);
        var state = await State(tenant, ct); if (state.Tenant.Revision != page.SecurityRevision) throw new SecurityConflictException("stale_revision");
        var items = new List<TaskView>(); foreach (var task in page.Items) items.Add(await TaskProjection.ForAsync(state, actor, task, iam, sessions, time.GetUtcNow(), ct, sources));
        var canDelegate = false;
        if (session is { } sourceId && await sessions.FindTenantSessionAsync(tenant, sourceId, ct) is { } source && source.Snapshot.ArchivedAt is null
            && source.Snapshot.LifecycleStatus == AgentCore.Domain.Conversation.SessionLifecycleStatus.Active
            && source.Snapshot.Status is not (AgentCore.Domain.Conversation.SessionStatus.Ended or AgentCore.Domain.Conversation.SessionStatus.Ending))
        {
            canDelegate = true;
            foreach (var key in new[] { "sessions.read", "sessions.manage", "tasks.delegate" })
                canDelegate &= (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(key), TenantSessionRules.Scope(tenant, sourceId), ct)).Allowed;
        }
        await Fence(tenant, state.Tenant.Revision, ct); return new(items, page.NextCursor, page.HasMore, state.Tenant.Revision, canDelegate);
    }
    public async ValueTask<TaskView> ReadAsync(Guid tenant, HumanSessionReference human, Guid taskId, CancellationToken ct)
    {
        var actor = new TaskActor(Human: human); var notices = await tasks.ReadTaskNotificationsAsync(tenant, actor, taskId, ct);
        var task = await tasks.FindTaskAsync(tenant, taskId, ct) ?? throw new AccessDeniedException("resource_unavailable"); var state = await State(tenant, ct);
        if (state.Tenant.Revision != notices.SecurityRevision) throw new SecurityConflictException("stale_revision");
        var view = await TaskProjection.ForAsync(state, actor, task, iam, sessions, time.GetUtcNow(), ct, sources); await Fence(tenant, state.Tenant.Revision, ct); return view;
    }
    public async ValueTask<object?> RecoverAsync(Guid tenant, Guid session, HumanSessionReference human, string key, CancellationToken ct)
    {
        var saved = await tasks.FindTaskRequestAsync(tenant, session, new(Human: human), TaskLimits.Key(key), ct);
        if (saved is not null) return new { task = new { saved.Task.TaskId, revision = 1 }, replayed = true, saved.SecurityRevision };
        if (security is ITenantApprovalStore approvalStore && await approvalStore.FindTaskDelegationApprovalAsync(tenant, $"human:{human.PrincipalId:D}:session:{session:D}", TaskLimits.Key(key), ct) is { } pending)
            return await ApprovalReceipt(pending, human, true, ct);
        return null;
    }
    public async ValueTask<object?> RecoverMessageAsync(Guid tenant, Guid task, HumanSessionReference human, string key, CancellationToken ct)
    {
        var saved = await messages.ReadMessageReceiptAsync(tenant, new(Human: human), task, key, ct);
        if (saved is null) return null; await Fence(tenant, saved.SecurityRevision, ct);
        return new { message = TenantTaskToolService.MessageView(saved.Message), saved.SecurityRevision, confirmed = true };
    }
    public async ValueTask<object?> RecoverCommandAsync(Guid tenant, Guid task, HumanSessionReference human, string key, string kind, Guid? notification, CancellationToken ct)
    {
        var saved = await tasks.ReadTaskCommandReceiptAsync(tenant, new(Human: human), task, kind, key, notification, ct);
        return saved is null ? null : new { confirmed = true, task = await ReadAsync(tenant, human, task, ct) };
    }
    public async ValueTask<object> DelegateAsync(Guid tenant, Guid session, HumanSessionReference human, TaskRequest request, long revision, CancellationToken ct)
    {
        if (approvals is not null)
        {
            var prepared = await approvals.PrepareHumanTaskDelegationAsync(tenant, session, human, request, revision, ct);
            if (prepared.Approval is { } approval) return await ApprovalReceipt(approval, human, prepared.Replayed, ct);
            var saved = prepared.Task ?? throw new AccessDeniedException("task_admission_unavailable");
            return new { task = new { saved.Task.TaskId, revision = 1 }, saved.Replayed, saved.SecurityRevision };
        }
        var result = await service.DelegateAsync(tenant, session, new(Human: human), request, revision, ct);
        return new { task = new { result.Task.TaskId, revision = 1 }, result.Replayed, result.SecurityRevision };
    }
    private async ValueTask<object> ApprovalReceipt(TenantApprovalRequest request, HumanSessionReference human, bool replayed, CancellationToken ct)
    {
        var canRead = false;
        if (approvals is not null)
            try { _ = await approvals.ReadViewAsync(request.TenantId, request.RequestId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), ct); canRead = true; }
            catch (AccessDeniedException) { }
        var state = await State(request.TenantId, ct);
        var outcome = security is ITenantHumanTaskApprovalStore owner ? await owner.FindHumanTaskApprovalOutcomeAsync(request.TenantId, request.Operation.OperationId, ct) : null;
        if (outcome?.TaskAdmission is { } admitted) return new { task = new { admitted.TaskId, revision = 1 }, securityRevision = state.Tenant.Revision, replayed = true };
        return new { approvalRequestId = request.RequestId, approvalStatus = request.Status.ToString(), canReadApproval = canRead,
            admissionState = outcome is not null ? "notAdmitted" : request.Status == TenantApprovalStatus.Pending ? "pendingReview"
                : request.Status == TenantApprovalStatus.Approved ? "awaitingAdmission" : "notAdmitted",
            reasonCode = outcome?.ReasonCode, securityRevision = state.Tenant.Revision, replayed };
    }
    public async ValueTask<object> CancelAsync(Guid tenant, Guid task, HumanSessionReference human, long revision, long securityRevision, string key, string? reason, CancellationToken ct)
    {
        var result = await tasks.CancelTaskAsync(new(tenant, task, new(Human: human), revision, securityRevision, key,
            reason is null ? null : TaskLimits.Text(reason, 160, "Reason"), ids.NewId()), ct);
        return new { task = new { result.Task.TaskId, result.Task.Revision, state = result.Task.State.ToString() }, result.Replayed, result.SecurityRevision };
    }
    public async ValueTask<object> MessagesAsync(Guid tenant, HumanSessionReference human, Guid? task, Guid? thread, Guid? cursor, int limit, CancellationToken ct)
    {
        var page = await messages.ReadMessagesPageAsync(tenant, new(Human: human), task, thread, cursor, limit, ct);
        var state = await State(tenant, ct); var items = new List<object>();
        foreach (var message in page.Items)
        {
            var saved = await messages.FindAgentMessageSourceAsync(tenant, message.MessageId, ct) ?? throw new AccessDeniedException("resource_unavailable");
            string? senderName = null;
            if (message.SenderInstanceId is { } sender) senderName = (await agents.ReadAsync(human.PrincipalId, tenant, sender, ct)).Instance.Persona.Name;
            else if (state.Users.SingleOrDefault(u => u.PrincipalId == message.SenderPrincipalId) is { } user
                && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("members.read"), AccessScope.ForResource(tenant, "member", user.UserId.ToString("D")), ct)).Allowed)
                senderName = (await security.ReadAccountAsync(user.AccountId, ct))?.DisplayName;
            var receipt = saved.Receipt;
            var wakeVisible = receipt.WakeSessionId is { } sessionId && await sessions.FindTenantSessionAsync(tenant, sessionId, ct) is { } wakeSession
                && !AgentCore.Domain.Conversation.SessionRetention.IsDue(wakeSession.Snapshot, time.GetUtcNow())
                && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(tenant, sessionId), ct)).Allowed
                && receipt.WakeRunId is { } runId && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agent-runs.read"), TenantRunRules.Scope(tenant, runId), ct)).Allowed;
            items.Add(new { message.MessageId, message.ThreadId, message.Sequence, senderKind = message.SenderKind.ToString(), message.SenderInstanceId,
                senderDisplayName = senderName, kind = message.Kind.ToString(), message.Text, message.CreatedAt,
                deliveryState = receipt.State.ToString(), deliveryReason = receipt.Reason, wakeSessionId = wakeVisible ? receipt.WakeSessionId : null, wakeRunId = wakeVisible ? receipt.WakeRunId : null });
        }
        await Fence(tenant, page.SecurityRevision, ct);
        return new { items, page.NextCursor, page.HasMore, page.SecurityRevision };
    }
    public async ValueTask<object> GuideAsync(Guid tenant, Guid task, HumanSessionReference human, string text, string key, long securityRevision, CancellationToken ct)
    {
        var result = await messages.AppendAgentMessageAsync(new(tenant, new(Human: human), task, null, null,
            AgentMessageKind.HumanGuidance, text, key, securityRevision, ids.NewId(), ids.NewId(), ids.NewId()), ct);
        return new { result.Thread.ThreadId, messageId = result.Message.MessageId, result.Message.Sequence, result.Replayed, result.SecurityRevision };
    }
    public async ValueTask<object> ThreadsAsync(Guid tenant, HumanSessionReference human, Guid instance, Guid? cursor, int limit, CancellationToken ct)
    {
        var page = await messages.ReadAgentThreadsAsync(new(tenant, new(Human: human), InstanceId: instance, Scope: "direct", Cursor: cursor, Limit: limit), ct);
        var items = new List<object>();
        foreach (var thread in page.Items)
            items.Add(new { thread.ThreadId, thread.FirstInstanceId, thread.SecondInstanceId, thread.CreatedAt,
                firstName = (await agents.ReadAsync(human.PrincipalId, tenant, thread.FirstInstanceId, ct)).Instance.Persona.Name,
                secondName = (await agents.ReadAsync(human.PrincipalId, tenant, thread.SecondInstanceId, ct)).Instance.Persona.Name });
        await Fence(tenant, page.SecurityRevision, ct);
        return new { items, page.NextCursor, page.HasMore, page.SecurityRevision };
    }
    public async ValueTask<object> NotificationsAsync(Guid tenant, HumanSessionReference human, Guid task, CancellationToken ct)
    {
        var page = await tasks.ReadTaskNotificationsAsync(tenant, new(Human: human), task, ct); await Fence(tenant, page.SecurityRevision, ct);
        return new { items = page.Items.Select(TenantTaskToolService.NotificationView), page.NextCursor, page.HasMore, page.SecurityRevision };
    }
    public async ValueTask<object> AcknowledgeAsync(Guid tenant, HumanSessionReference human, Guid task, Guid notification, long revision, long securityRevision, string key, CancellationToken ct)
    {
        var saved = await tasks.AcknowledgeTaskAsync(new(tenant, task, notification, new(Human: human), revision, securityRevision, key, ids.NewId()), ct);
        return TenantTaskToolService.NotificationView(saved);
    }
    public async ValueTask<object> TargetsAsync(Guid tenant, HumanSessionReference human, Guid sessionId, int page, CancellationToken ct)
    {
        // The store authenticates the human reference and current source read before target discovery.
        _ = await tasks.ReadTasksAsync(new(tenant, new(Human: human), SessionId: sessionId, Limit: 1), ct);
        var state = await State(tenant, ct); var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        foreach (var key in new[] { "sessions.read", "tasks.delegate", "sessions.manage" }) await TaskAuthority.RequireAsync(state, human.PrincipalId, key, TenantSessionRules.Scope(tenant, sessionId), iam, ct);
        var candidates = await agents.ListAsync(human.PrincipalId, tenant, page, 50, ct); var result = new List<object>();
        async ValueTask<object> SpaceOption(AgentCore.Domain.Spaces.Space space, TenantAgent worker)
        {
            var environments = new List<object>();
            if (space.EnvironmentsEnabled && spaces is ITenantEnvironmentStore environmentStore
                && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("environments.read"), TenantSpaceService.Scope(tenant, space.SpaceId), ct)).Allowed
                && (await iam.CheckOwnedTenantAsync(state, worker.PrincipalId, PermissionCatalog.Get("environments.read"), TenantSpaceService.Scope(tenant, space.SpaceId), ct)).Allowed)
                foreach (var environment in await environmentStore.ListEnvironmentsAsync(tenant, space.SpaceId, ct))
                    if (environment.DeletedAt is null) environments.Add(new { environmentId = environment.EnvironmentId, name = environment.Name });
            return new { spaceId = space.SpaceId, name = space.Name, environments };
        }
        // Filter before paging, then walk every authorized page once for this discovery request.
        // A worker's Private Space is not a prerequisite for a mutually usable Shared Space.
        IReadOnlyList<Guid>? allowedSpaces = null;
        if (!(await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("spaces.read"), AccessScope.ForTenant(tenant), ct)).Allowed)
        {
            var allowed = new List<Guid>();
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId).Select(a => a.Scope).Distinct())
                if (scope.ResourceType == "space" && Guid.TryParseExact(scope.ResourceId, "D", out var id)
                    && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("spaces.read"), scope, ct)).Allowed) allowed.Add(id);
            allowedSpaces = allowed;
        }
        var sharedOptions = new List<(AgentCore.Domain.Spaces.Space Space, IReadOnlyList<AgentCore.Domain.Spaces.SpaceMembership> Members)>();
        for (var spacePage = 0; ; spacePage++)
        {
            ct.ThrowIfCancellationRequested();
            var catalog = await spaces.ListSpacesAsync(tenant, allowedSpaces, spacePage, 100, ct);
            foreach (var shared in catalog.Items.Where(s => s.Kind == AgentCore.Domain.Spaces.SpaceKind.Shared && s.DeletedAt is null))
                sharedOptions.Add((shared, await spaces.ReadMembershipsAsync(tenant, shared.SpaceId, ct)));
            if ((long)(spacePage + 1) * catalog.PageSize >= catalog.Total || catalog.Items.Count == 0) break;
        }
        async ValueTask<bool> ReadableSpace(AgentCore.Domain.Spaces.Space space, TenantAgent worker) => space.DeletedAt is null
            && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("spaces.read"), TenantSpaceService.Scope(tenant, space.SpaceId), ct)).Allowed
            && (await iam.CheckOwnedTenantAsync(state, worker.PrincipalId, PermissionCatalog.Get("spaces.read"), TenantSpaceService.Scope(tenant, space.SpaceId), ct)).Allowed;
        foreach (var candidate in candidates.Items)
            try
            {
                await TaskAuthority.TargetAsync(state, human.PrincipalId, candidate, iam, ct);
                var own = await spaces.FindPrivateSpaceAsync(tenant, candidate.Instance.InstanceId, ct); if (own is null) continue;
                var options = new List<object>(); Guid? defaultSpace = null;
                if (await ReadableSpace(own, candidate)
                    && (await spaces.ReadMembershipsAsync(tenant, own.SpaceId, ct)).Any(m => m.InstanceId == candidate.Instance.InstanceId))
                { options.Add(await SpaceOption(own, candidate)); defaultSpace = own.SpaceId; }
                foreach (var (shared, members) in sharedOptions)
                    if (members.Any(m => m.InstanceId == candidate.Instance.InstanceId) && await ReadableSpace(shared, candidate))
                    { options.Add(await SpaceOption(shared, candidate)); defaultSpace ??= shared.SpaceId; }
                if (defaultSpace is null) continue;
                object? budget = null; object? model = null;
                if (configurations is not null)
                {
                    var current = await configurations.CurrentPolicyAsync(tenant, candidate.Instance.InstanceId, ct);
                    var ceiling = AgentCore.Application.Execution.ExecutionBudgetResolver.Resolve(current.Definition, current.Agent.Instance.ExecutionBudgets, false).Profile;
                    budget = new { ceiling.MaxSteps, ceiling.DurationSeconds };
                    if (models is not null && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("models.read"), AccessScope.ForTenant(tenant), ct)).Allowed)
                    {
                        var preference = current.Agent.Instance.UnattendedModel ?? AgentCore.Domain.Models.ModelPreference.Inherit;
                        if (preference.Mode == "auto") model = new { mode = "auto", label = "Automatic selection at assignment" };
                        else
                        {
                            var selected = AgentCore.Application.Models.SessionModelBinder.BindPreference(await models.ReadAsync(state, ct), preference, current.Definition.ModelDefaults);
                            model = new { mode = preference.Mode, label = selected.Binding?.ModelLabel ?? selected.ExternalModelId };
                        }
                    }
                }
                result.Add(new { instanceId = candidate.Instance.InstanceId, name = candidate.Instance.Persona.Name, defaultSpaceId = defaultSpace.Value, spaces = options, budget, model });
            }
            catch (AccessDeniedException) { }
        await Fence(tenant, state.Tenant.Revision, ct);
        return new { items = result, hasMore = (page + 1) * candidates.PageSize < candidates.Total, page, securityRevision = state.Tenant.Revision };
    }
}
