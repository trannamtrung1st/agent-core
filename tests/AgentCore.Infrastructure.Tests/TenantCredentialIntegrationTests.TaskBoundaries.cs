using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tasks_dependency_cancel_deadline_source_retirement_and_private_projection_follow_actual_saved_facts(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-27T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Boundaries", "boundaries");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Explicit worker admission", ["sessions.create", "agent-runs.create", "agent-messages.read", "agents.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        agent = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, new(AcceptTasks: true, MaxConcurrentTasks: 8), await Revision(), agent.Instance.Revision, initiator: human);
        var source = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var service = TaskService(f); var store = (ITenantTaskStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam; var actor = new TaskActor(Human: human);
        async Task<DelegatedTask> Assign(string key, Guid[]? dependencies = null, DateTimeOffset? deadline = null, bool report = false) =>
            (await service.DelegateAsync(tenant, source.Owner.SessionId, actor, new("Inspect approved migration facts", key, [agent.Instance.InstanceId], false, null, null, dependencies ?? [], report, deadline, key), await Revision())).Task;
        var first = await Assign("prerequisite"); var dependent = await Assign("dependent", [first.TaskId], report: true);
        Assert.Equal(DelegatedTaskState.PendingDependencies, dependent.State); Assert.Null(dependent.WorkerSessionId); Assert.Null(dependent.InitialWorkerRunId);
        Assert.False(await store.ReconcileTaskAsync(tenant, dependent.TaskId, dependent.Revision, f.Ids.NewId()));

        var account = await f.Identity.RegisterAsync("observer@example.test", "Observer", "strong-observer-password");
        var observerRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Task status observer", ["tasks.read", "agents.read"], await Revision());
        var invitation = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, observerRole.RoleId, await Revision());
        var observer = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var readRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Exact source Session read", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, observer.PrincipalId, readRole.RoleId, TenantSessionRules.Scope(tenant, source.Owner.SessionId), await Revision());
        var observerLogin = (await f.Identity.SignInAsync(account.LoginKey, "strong-observer-password"))!.Session;
        var observerActor = new TaskActor(Human: new(account.AccountId, observerLogin.SessionId, observer.PrincipalId));
        Assert.Equal(2, (await store.ReadTasksAsync(new(tenant, observerActor, SessionId: source.Owner.SessionId))).Items.Count);
        var view = await TaskProjection.ForAsync((await f.Iam.ReadTenantAsync(tenant))!, observerActor, first, f.Authorizer, (ITenantSessionStore)f.Iam, clock.GetUtcNow(), default);
        Assert.Null(view.SpaceName); Assert.Null(view.EnvironmentName); Assert.Null(view.EnvironmentConfigured);
        Assert.Null(view.WorkerSessionId); Assert.Null(view.InitialRunId); Assert.Null(view.TerminalRunId); Assert.Null(view.Result); Assert.False(view.CanCancel); Assert.False(view.CanSend);

        var cancel = new TaskCancel(tenant, first.TaskId, actor, first.Revision, await Revision(), "cancel-once", "Stop the exact assignment", f.Ids.NewId());
        var cancelled = await store.CancelTaskAsync(cancel); Assert.True(cancelled.Task.CancellationRequested); Assert.True((await store.CancelTaskAsync(cancel)).Replayed);
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.CancelTaskAsync(cancel with { Reason = "Changed cancellation" }).AsTask());
        Assert.True(await store.ReconcileTaskAsync(tenant, first.TaskId, cancelled.Task.Revision, f.Ids.NewId()));
        Assert.Equal(DelegatedTaskState.Cancelled, (await store.FindTaskAsync(tenant, first.TaskId))!.State);
        Assert.True(await store.ReconcileTaskAsync(tenant, dependent.TaskId, dependent.Revision, f.Ids.NewId()));
        var blocked = (await store.FindTaskAsync(tenant, dependent.TaskId))!;
        Assert.Equal(DelegatedTaskState.Blocked, blocked.State); Assert.Equal("dependency_failed", blocked.Reason); Assert.Null(blocked.WorkerSessionId);
        var notice = Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, blocked.TaskId)).Items);
        Assert.Null(notice.TerminalRunId); Assert.Null(notice.HandlingRunId);
        var coordination = new TenantTaskCoordinationService(store, f.Iam, f.RunConfiguration(),
            new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), f.Ids, clock, f.Authorizer,
            (ITenantAgentMessageStore)f.Iam, (ITenantSpaceStore)f.Iam);
        await coordination.ReconcileAsync(32, default); await coordination.ReconcileAsync(32, default);
        notice = Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, blocked.TaskId)).Items);
        Assert.Null(notice.TerminalRunId); Assert.Null(notice.HandlingRunId); Assert.Equal("task_report_no_worker_run", notice.WithheldReason);
        var observerView = await TaskProjection.ForAsync((await f.Iam.ReadTenantAsync(tenant))!, observerActor, blocked, f.Authorizer,
            (ITenantSessionStore)f.Iam, clock.GetUtcNow(), default);
        Assert.False(observerView.CanAcknowledge);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.AcknowledgeTaskAsync(new(tenant, blocked.TaskId, notice.NotificationId,
            observerActor, notice.Revision, Revision().Result, "read-only-cannot-handle", f.Ids.NewId())).AsTask());
        Assert.Equal(notice.Revision, Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, blocked.TaskId)).Items).Revision);
        var acknowledgment = new TaskAcknowledge(tenant, blocked.TaskId, notice.NotificationId, actor, notice.Revision,
            await Revision(), "acknowledge-blocked", f.Ids.NewId());
        Assert.NotNull((await store.AcknowledgeTaskAsync(acknowledgment)).AcknowledgedAt);
        Assert.Equal(await store.AcknowledgeTaskAsync(acknowledgment), await store.AcknowledgeTaskAsync(acknowledgment));

        var blocker = await Assign("still-pending"); var cancelledDependent = await Assign("cancel-pending", [blocker.TaskId]);
        cancelledDependent = (await store.CancelTaskAsync(new(tenant, cancelledDependent.TaskId, actor, cancelledDependent.Revision,
            await Revision(), "cancel-pending-once", "No worker should start", f.Ids.NewId()))).Task;
        Assert.Equal(DelegatedTaskState.Blocked, cancelledDependent.State);
        var cancelledNotice = Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, cancelledDependent.TaskId)).Items);
        Assert.Null(cancelledNotice.TerminalRunId); Assert.Null(cancelledDependent.InitialWorkerRunId);

        var expiring = await Assign("deadline", deadline: clock.GetUtcNow().AddMinutes(1)); clock.Advance(TimeSpan.FromMinutes(2));
        var expiredWorker = (await runs.FindTenantRunAsync(tenant, expiring.InitialWorkerRunId!.Value))!;
        expiredWorker = await runtime.ApplyTenantRuntimeCommandAsync(expiredWorker.Owner, expiredWorker.AgentRunId,
            new AgentRunCommand.Claim(expiredWorker.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        Assert.Equal(0, expiredWorker.AttemptCount); Assert.Equal(AgentRunStatus.Cancelled, expiredWorker.Status);
        Assert.True(await store.ReconcileTaskAsync(tenant, expiring.TaskId, expiring.Revision, f.Ids.NewId()));
        var expired = (await store.FindTaskAsync(tenant, expiring.TaskId))!;
        Assert.Equal(DelegatedTaskState.Failed, expired.State); Assert.Equal("task_deadline_exceeded", expired.Reason);

        var orphan = await Assign("source-retired");
        var messageStore = (ITenantAgentMessageStore)f.Iam;
        var guidance = await messageStore.AppendAgentMessageAsync(new(tenant, actor, orphan.TaskId, null, null, AgentMessageKind.HumanGuidance,
            "Retire this correspondence with its source", "retained-guidance", await Revision(), f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => messageStore.AppendAgentMessageAsync(new(tenant, actor, null, null, agent.Instance.InstanceId,
            AgentMessageKind.Note, "A human cannot speak as an Agent", "no-impersonation", Revision().Result, f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId())).AsTask());
        await f.Sessions.ChangeAsync(tenant, human, new(source.Owner.SessionId, source.Revision, TenantSessionAction.Delete), await Revision());
        Assert.Null(await store.FindTaskAsync(tenant, orphan.TaskId)); Assert.Empty((await store.ReadTasksAsync(new(tenant, actor, SessionId: source.Owner.SessionId))).Items);
        Assert.Null(await messageStore.FindAgentMessageSourceAsync(tenant, guidance.Message.MessageId));
        Assert.True(await store.ReconcileTaskAsync(tenant, orphan.TaskId, orphan.Revision, f.Ids.NewId()));
        Assert.Null(await runs.FindTenantRunAsync(tenant, orphan.InitialWorkerRunId!.Value));
        var retirement = await ((ITenantSessionStore)f.Iam).FindExpiredSessionAsync(tenant, orphan.WorkerSessionId!.Value);
        Assert.NotNull(retirement);
        Assert.False(await ((ITenantSessionStore)f.Iam).RetireDueSessionAsync(retirement with { DueAtUtc = retirement.DueAtUtc.AddMilliseconds(1) }, f.Ids.NewId()));
        Assert.True(await ((ITenantSessionStore)f.Iam).RetireDueSessionAsync(retirement, f.Ids.NewId()));
        Assert.Contains(await f.Iam.ReadAuditAsync(tenant, 100), audit => audit.Action == "tasks.settle"
            && audit.Target == orphan.TaskId.ToString("D") && audit.ReasonCode == "Cancelled");
        if (sqlite)
        {
            await using var retained = f.Contexts!.CreateDbContext();
            var row = await retained.AgentRuns.SingleAsync(r => r.AgentRunId == orphan.InitialWorkerRunId.Value.ToString("D"));
            var retiredTask = await retained.Set<AgentCore.Infrastructure.Persistence.DelegatedTaskRow>().SingleAsync(t => t.TaskId == orphan.TaskId);
            var savedTask = System.Text.Json.JsonSerializer.Deserialize<DelegatedTask>(retiredTask.Json)!;
            Assert.NotNull(savedTask.ContentRetiredAt); Assert.Equal("[retired]", savedTask.Request.Objective); Assert.Equal(orphan.Request.Fingerprint(), savedTask.Request.Fingerprint());
            Assert.Equal(orphan.Source, savedTask.Source); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(orphan.Assignment), System.Text.Json.JsonSerializer.Serialize(savedTask.Assignment));
            var retiredMessage = await retained.Set<AgentCore.Infrastructure.Persistence.AgentMessageRow>().SingleAsync(m => m.MessageId == guidance.Message.MessageId);
            var savedMessage = System.Text.Json.JsonSerializer.Deserialize<AgentMessage>(retiredMessage.Json)!;
            Assert.Equal("[retired]", savedMessage.Text); Assert.NotNull(savedMessage.ContentRetiredAt); Assert.Equal(guidance.Message.RequestHash, savedMessage.RequestHash);
            Assert.Equal((int)AgentRunStatus.Cancelled, row.Status);
            using var payload = System.Text.Json.JsonDocument.Parse(row.PayloadJson); Assert.Equal(0, payload.RootElement.GetProperty("attemptCount").GetInt32());
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => messageStore.ReadAgentMessagesAsync(tenant, actor, guidance.Thread.ThreadId, 0, 50).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => messageStore.ReadMessagesPageAsync(tenant, actor, dependent.TaskId, null, null, 1).AsTask());
    }
}
