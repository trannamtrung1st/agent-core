using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Goals;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Application.Sessions;
using AgentCore.Application.Events;
using AgentCore.Application.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text.Json;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    internal sealed partial class Fixture { internal List<string> DatabaseErrors { get; } = []; }
    private sealed class TaskDatabaseFailureCapture(List<string> errors) : DbCommandInterceptor
    {
        public override void CommandFailed(DbCommand command, CommandErrorEventData data) => errors.Add(data.Exception.GetBaseException().Message);
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken ct = default)
        { errors.Add(data.Exception.GetBaseException().Message); return Task.CompletedTask; }
    }
    private static TenantTaskService TaskService(Fixture f) => new(f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam,
        (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), f.Authorizer, f.Ids, f.Clock);

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Tasks_human_cross_Agent_atomic_admission_replay_conflict_messages_and_reopen(bool sqlite, bool report, bool temporary)
    {
        var temporaryClock = temporary ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var f = await Fixture.Create(sqlite, clock: temporaryClock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Tasks", "tasks");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var source = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(human.PrincipalId, tenant, "Task reviewer", await Revision(), initiator: human);
        draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with {
            ExecutionPolicy = new(Capabilities: new("Selected", ["tasks.messages.send", "tasks.messages.list", "execution.wait"])) }, await Revision(), draft.Revision, initiator: human);
        await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
        var target = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, target.PrincipalId);
        var executionRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Task worker", ["sessions.create", "agent-runs.create", "sessions.read", "agent-runs.read", "agents.read", "tasks.read", "agent-messages.read", "agent-messages.send"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, target.PrincipalId, executionRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, source.PrincipalId);
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, source.PrincipalId, executionRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, human, SessionMode.Text, await Revision(), source.Instance.Revision,
            retentionKind: temporary ? SessionRetentionKind.Temporary : SessionRetentionKind.Saved);
        var service = services.GetRequiredService<TenantTaskService>(); var actor = new TaskActor(Human: human);
        var goalId = f.Ids.NewId();
        var goalService = GoalService(f);
        var independentGoal = (await goalService.MutateAsync(tenant, session.Owner.SessionId, goalId, human,
            new(GoalCommandKind.Create, "independent-goal", await Revision(), 0, 0, HumanGoal(), Confirm: true))).Goal;
        Assert.Equal(GoalStatus.Active, independentGoal.Status);
        TaskRequest Request(string objective = "synthetic-task-worker synthetic-task-question: Compare the two approved migration approaches") => new(objective, "Review migration", [target.Instance.InstanceId], false, null, null, [], report, null, "assign-once");
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => service.DelegateAsync(tenant, session.Owner.SessionId, actor, Request(), Revision().Result).AsTask());
        Assert.Equal("task_target_unavailable", denied.ReasonCode);
        target = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, target.Instance.InstanceId, new(AcceptTasks: true), await Revision(), target.Instance.Revision, initiator: human);
        var beforePreparation = await Revision();
        var preparation = await service.PrepareAsync(tenant, session.Owner.SessionId, actor, Request(), beforePreparation);
        Assert.Null(preparation.Replay); Assert.NotNull(preparation.Admission);
        Assert.Equal(beforePreparation, await Revision());
        Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, preparation.Admission.Task.TaskId));
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, preparation.Admission.WorkerSession!.SessionId));
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, preparation.Admission.WorkerRun!.AgentRunId));
        var reserved = new PreparedTaskDelegation(preparation.Admission.Task, null, preparation.Admission.WorkerSession.Entries.Single().EntryId,
            preparation.Admission.WorkerRun.ActivationId, preparation.Admission.WorkerRun.ResponseId);
        var consent = TenantApprovalRules.PrepareTaskDelegation(f.Ids.NewId(), source.Instance.InstanceId, reserved, source.Instance.Revision, source.Instance.PersonaRevision);
        Assert.Equal("tasks.delegate", consent.Permission); Assert.Equal("session", consent.ResourceType); Assert.Equal(session.Owner.SessionId.ToString("D"), consent.ResourceId);
        Assert.Equal(consent, TenantApprovalRules.Rebuild(consent));
        var roundTrip = JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(consent))!;
        Assert.Equal(roundTrip, TenantApprovalRules.Rebuild(roundTrip));
        Assert.Equal(consent.ActionHash, roundTrip.ActionHash);
        var consentRequest = new TenantApprovalRequest(f.Ids.NewId(), tenant, preparation.Admission.WorkerRun.AgentRunId,
            session.Owner.SessionId, reserved.Task.Source.RootActivationId, human.PrincipalId, human.PrincipalId, ApprovalKind.Review,
            consent, TenantApprovalStatus.Pending, 1, f.Clock.GetUtcNow(), f.Clock.GetUtcNow().AddHours(1));
        TenantApprovalRules.MatchTaskDelegationRequest(consentRequest, Request());
        Assert.Throws<SecurityConflictException>(() => TenantApprovalRules.MatchTaskDelegationRequest(consentRequest, Request("Changed while awaiting consent")));
        Assert.Equal(beforePreparation, await Revision());
        TaskMutationResult admitted;
        if (!report && !temporary)
        {
            var competing = await service.PrepareAsync(tenant, session.Owner.SessionId, actor, Request(), await Revision());
            var store = (ITenantTaskStore)f.Iam;
            var results = await Task.WhenAll(Task.Run(async () => await store.AdmitTaskAsync(preparation.Admission)),
                Task.Run(async () => await store.AdmitTaskAsync(competing.Admission!)));
            Assert.Single(results, result => !result.Replayed); Assert.Single(results, result => result.Replayed);
            Assert.Equal(results[0].Task.TaskId, results[1].Task.TaskId);
            Assert.Equal(results[0].Task.InitialWorkerRunId, results[1].Task.InitialWorkerRunId);
            admitted = results.Single(result => !result.Replayed);
        }
        else admitted = await service.DelegateAsync(tenant, session.Owner.SessionId, actor, Request(), await Revision());
        var task = admitted.Task;
        Assert.Equal(DelegatedTaskState.Queued, task.State); Assert.NotNull(task.WorkerSessionId); Assert.NotEqual(session.Owner.SessionId, task.WorkerSessionId);
        var runs = (ITenantRunStore)f.Iam; var worker = (await runs.FindTenantRunAsync(tenant, task.InitialWorkerRunId!.Value))!;
        Assert.Equal(target.PrincipalId, worker.Owner.ExecutorPrincipalId); Assert.Equal(target.Instance.InstanceId, worker.AgentInstanceId);
        Assert.Equal(target.Instance.DefinitionId, worker.DefinitionId); Assert.Equal(target.Instance.ActiveVersion, worker.DefinitionVersion);
        Assert.Null(worker.Admission.ExecutionContext!.ParentRunId); Assert.Equal(worker.ActivationId, worker.Admission.Activation.RootActivationId);
        var privateSpace = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, target.Instance.InstanceId))!;
        Assert.Equal(privateSpace.SpaceId, worker.Admission.Configuration!.SpaceContext!.SpaceId);
        Assert.Equal(worker.AgentRunId, (await TenantExecutionLineage.RootAsync(worker, runs, default)).AgentRunId);
        var replay = await service.DelegateAsync(tenant, session.Owner.SessionId, actor, Request(), admitted.SecurityRevision - 1);
        Assert.True(replay.Replayed); Assert.Equal(task.TaskId, replay.Task.TaskId); Assert.Equal(task.InitialWorkerRunId, replay.Task.InitialWorkerRunId);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.DelegateAsync(tenant, session.Owner.SessionId, actor, Request("Different objective"), Revision().Result).AsTask());
        var messages = (ITenantAgentMessageStore)f.Iam;
        var command = new AgentMessageCommand(tenant, actor, task.TaskId, null, null, AgentMessageKind.HumanGuidance, "Use the approved source only", "guidance-once", await Revision(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var message = await messages.AppendAgentMessageAsync(command); Assert.Equal(TaskActorKind.Human, message.Message.SenderKind); Assert.Null(message.Message.SenderInstanceId);
        var repeated = await messages.AppendAgentMessageAsync(command); Assert.True(repeated.Replayed); Assert.Equal(message.Message.MessageId, repeated.Message.MessageId);
        await Assert.ThrowsAsync<SecurityConflictException>(() => messages.AppendAgentMessageAsync(command with { Text = "Changed text" }).AsTask());
        Assert.Equal("Use the approved source only", Assert.Single((await messages.ReadAgentMessagesAsync(tenant, actor, message.Thread.ThreadId, 0, 50)).Items).Text);
        var page = await ((ITenantTaskStore)f.Iam).ReadTasksAsync(new(tenant, actor, SessionId: session.Owner.SessionId)); Assert.Equal(task.TaskId, Assert.Single(page.Items).TaskId);
        DelegatedTask? dependent = null;
        if (!report && !temporary)
        {
            dependent = (await service.DelegateAsync(tenant, session.Owner.SessionId, actor,
                new("synthetic-task-worker Follow the completed migration review", "Follow reviewed evidence", [target.Instance.InstanceId], false,
                    null, null, [task.TaskId], false, null, "after-success"), await Revision())).Task;
            Assert.Equal(DelegatedTaskState.PendingDependencies, dependent.State); Assert.Null(dependent.WorkerSessionId);
        }
        var storage = (ITenantRuntimeStore)f.Iam;
        worker = await storage.ApplyTenantRuntimeCommandAsync(worker.Owner, worker.AgentRunId,
            new AgentRunCommand.Claim(worker.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(worker.Owner, worker.SessionId, worker.AgentRunId, worker.Claim!.Generation);
        var receiptBudgetRevision = await Revision();
        await Assert.ThrowsAsync<ArgumentException>(() => services.GetRequiredService<TenantTaskToolService>().ExecuteAsync(frame,
            new("bounded-mutation", "tasks.messages.send", JsonSerializer.Serialize(new { taskId = task.TaskId, text = "Must not be saved", kind = "note", requestKey = "insufficient-receipt-budget" })), 32, default).AsTask());
        Assert.Equal(receiptBudgetRevision, await Revision());
        Assert.Single((await messages.ReadAgentMessagesAsync(tenant, actor, message.Thread.ThreadId, 0, 50)).Items);
        var output = new CapturingSessionOutput(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output,
            frame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(worker.AgentRunId, true, deadline.Token));
            await runtime.WaitUntilIdleAsync(deadline.Token);
        }
        worker = (await runs.FindTenantRunAsync(tenant, worker.AgentRunId))!;
        Assert.True(worker.Status == AgentRunStatus.WaitingForSignal, $"Expected task-message wait; {worker.Status} {worker.Failure?.Code}\n{string.Join("\n", probe.Diagnostics)}\n{string.Join("\n", f.DatabaseErrors)}");
        Assert.Equal(task.TaskId, Assert.Single(worker.Wait!.Tasks!).TaskId);
        var answer = command with { Text = "Use the reviewed migration source.", RequestKey = "answer-once", ExpectedSecurityRevision = await Revision(), MessageId = Guid.NewGuid(), AuditId = Guid.NewGuid() };
        await messages.AppendAgentMessageAsync(answer);
        worker = await storage.ApplyTenantRuntimeCommandAsync(worker.Owner, worker.AgentRunId, new AgentRunCommand.ResumeWait(worker.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        frame = new(worker.Owner, worker.SessionId, worker.AgentRunId, worker.Claim!.Generation);
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, new CapturingSessionOutput(),
            frame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(worker.AgentRunId, true, deadline.Token)); await runtime.WaitUntilIdleAsync(deadline.Token);
        }
        worker = (await runs.FindTenantRunAsync(tenant, worker.AgentRunId))!;
        Assert.True(worker.Status == AgentRunStatus.Completed, $"{worker.Status} {worker.Failure?.Code}: {worker.Failure?.Summary}\n{string.Join("\n", probe.Diagnostics)}\n{string.Join("\n", f.DatabaseErrors)}");
        Assert.Equal(1, worker.AttemptCount); Assert.Equal(1, worker.WaitCount);
        var correspondence = await messages.ReadAgentMessagesAsync(tenant, actor, message.Thread.ThreadId, 0, 50);
        Assert.Equal(3, correspondence.Items.Count); Assert.Equal(target.PrincipalId, correspondence.Items.Single(m => m.SenderKind == TaskActorKind.Agent).SenderPrincipalId);
        Assert.Contains("Synthetic worker completed", worker.Result!.Text); Assert.Empty(http.Requests);
        Assert.True(await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, f.Ids.NewId()));
        var settled = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId))!; Assert.Equal(DelegatedTaskState.Succeeded, settled.State);
        Assert.Equal(worker.AgentRunId, settled.TerminalRunId);
        var coordination = services.GetRequiredService<TenantTaskCoordinationService>();
        await coordination.ReconcileAsync(32, default); await coordination.ReconcileAsync(32, default);
        if (dependent is not null)
        {
            var ready = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, dependent.TaskId))!;
            Assert.True(ready.State == DelegatedTaskState.Queued, $"{ready.State}: {ready.Reason}"); Assert.NotNull(ready.InitialWorkerRunId);
            var actual = await ExecuteCooperation(f, services, tenant, ready.InitialWorkerRunId.Value, probe);
            Assert.Equal(target.PrincipalId, actual.Owner.ExecutorPrincipalId); Assert.Equal(ready.TaskId, actual.Admission.DelegatedTask!.TaskId);
            Assert.True(await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, ready.TaskId, ready.Revision, f.Ids.NewId()));
            Assert.Equal(DelegatedTaskState.Succeeded, (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, ready.TaskId))!.State);
            Assert.Equal(actual.AgentRunId, Assert.Single((await ((ITenantTaskStore)f.Iam).ReadTaskNotificationsAsync(tenant, actor, ready.TaskId)).Items).TerminalRunId);
        }
        var afterReport = await ((ITenantTaskStore)f.Iam).ReadTaskNotificationsAsync(tenant, actor, task.TaskId);
        if (report)
        {
            var reportNotice = Assert.Single(afterReport.Items); Assert.Null(reportNotice.WithheldReason); Assert.NotNull(reportNotice.HandlingRunId);
            var parentRun = (await runs.FindTenantRunAsync(tenant, reportNotice.HandlingRunId!.Value))!;
            Assert.Equal(source.PrincipalId, parentRun.Owner.ExecutorPrincipalId); Assert.Equal(source.Instance.InstanceId, parentRun.AgentInstanceId);
            Assert.Equal(session.Owner.SessionId, parentRun.SessionId); Assert.Equal(source.Instance.DefinitionId, parentRun.DefinitionId);
            Assert.Equal(task.TaskId, parentRun.Admission.TaskReport!.TaskId);
            Assert.Equal(8, parentRun.Admission.ExecutionBudget!.Profile.MaxSteps); Assert.Equal(60, parentRun.Admission.ExecutionBudget.Profile.DurationSeconds);
            Assert.Equal(task.Assignment.Budget!.RootMaxSteps, worker.Admission.ExecutionBudget!.Profile.MaxSteps + parentRun.Admission.ExecutionBudget.Profile.MaxSteps);
            Assert.Equal(task.Assignment.Budget.RootDurationSeconds, worker.Admission.ExecutionBudget.Profile.DurationSeconds + parentRun.Admission.ExecutionBudget.Profile.DurationSeconds);
            var sourceFact = new TaskReportSource(settled, reportNotice, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!, worker);
            var resolvedReport = new ResolvedAgentRunConfiguration(parentRun.Admission.Configuration!, parentRun.PinnedPersona, parentRun.PinnedSkillCatalog, source.Instance.ExecutionBudgets);
            var counterfeit = TaskReportRules.Create(sourceFact, resolvedReport, parentRun.PinnedModel, f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId(), f.Clock.GetUtcNow());
            await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantTaskStore)f.Iam).RequireTaskReportSourcesAsync(counterfeit).AsTask());
            var currentSource = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, source.Instance.InstanceId))!;
            Assert.Throws<SecurityConflictException>(() => TaskReportRules.Shape(sourceFact, parentRun,
                currentSource with { Instance = currentSource.Instance with { Revision = currentSource.Instance.Revision + 1 } }, sourceFact.Parent!.Snapshot.Revision, f.Clock.GetUtcNow()));
            Assert.Equal(worker.AgentRunId, (await TenantExecutionLineage.RootAsync(parentRun, runs, default)).AgentRunId);
            parentRun = await storage.ApplyTenantRuntimeCommandAsync(parentRun.Owner, parentRun.AgentRunId,
                new AgentRunCommand.Claim(parentRun.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            var parentFrame = new TenantRuntimeFrame(parentRun.Owner, parentRun.SessionId, parentRun.AgentRunId, parentRun.Claim!.Generation);
            await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(parentFrame))!, new CapturingSessionOutput(),
                parentFrame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
            {
                Assert.True(await runtime.DispatchAgentRunAsync(parentRun.AgentRunId, true, deadline.Token)); await runtime.WaitUntilIdleAsync(deadline.Token);
            }
            parentRun = (await runs.FindTenantRunAsync(tenant, parentRun.AgentRunId))!;
            Assert.True(parentRun.Status == AgentRunStatus.Completed, $"Task report {parentRun.Status}: {parentRun.Failure?.Code} {parentRun.Failure?.Summary}\n{string.Join("\n", probe.Diagnostics)}\n{string.Join("\n", f.DatabaseErrors)}");
            Assert.Contains("Task Succeeded", parentRun.Result!.Text);
        }
        else Assert.Null(Assert.Single(afterReport.Items).HandlingRunId);
        var notices = await ((ITenantTaskStore)f.Iam).ReadTaskNotificationsAsync(tenant, actor, task.TaskId);
        var notice = Assert.Single(notices.Items);
        var ack = new TaskAcknowledge(tenant, task.TaskId, notice.NotificationId, actor, notice.Revision, notices.SecurityRevision, "notice-once", f.Ids.NewId());
        Assert.NotNull((await ((ITenantTaskStore)f.Iam).AcknowledgeTaskAsync(ack)).AcknowledgedAt);
        Assert.Equal(notice.Revision + 1, (await ((ITenantTaskStore)f.Iam).AcknowledgeTaskAsync(ack)).Revision);
        var unchangedGoal = await goalService.ReadAsync(tenant, session.Owner.SessionId, goalId, human);
        Assert.Equal(independentGoal.Revision, unchangedGoal.Revision);
        Assert.Equal(GoalStatus.Active, unchangedGoal.Status);
        Assert.Equal(GoalCheckpointStatus.Pending, unchangedGoal.Checkpoints.Single().Status);
        Assert.Empty(unchangedGoal.Checkpoints.Single().Evidence ?? []);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(task.InitialWorkerRunId, (await ((ITenantTaskStore)reopened).FindTaskAsync(tenant, task.TaskId))!.InitialWorkerRunId);
            Assert.Contains((await ((ITenantAgentMessageStore)reopened).ReadAgentMessagesAsync(tenant, actor, message.Thread.ThreadId, 0, 50)).Items, m => m.MessageId == message.Message.MessageId);
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            var rollback = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261027040000_SessionGoals"));
            Assert.Contains("restore_coordinated_task_backup_required", rollback.Message);
            Assert.Equal("20261027060000_TaskDelegation", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        if (temporary)
        {
            Assert.NotNull(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, worker.SessionId));
            temporaryClock!.Advance(TimeSpan.FromHours(24));
            Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, worker.SessionId));
            Assert.Null(await runs.FindTenantRunAsync(tenant, worker.AgentRunId));
            Assert.Empty(await runs.ListTenantRunIdsAsync(tenant, worker.SessionId));
            var visible = await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(tenant, null, 0, 50, surface: SessionSurface.BackgroundWork, instanceId: target.Instance.InstanceId);
            Assert.Empty(visible.Items); Assert.Equal(0, visible.Total);
            if (sqlite)
            {
                await using var retained = f.Contexts!.CreateDbContext();
                Assert.True(await retained.Sessions.AnyAsync(s => s.SessionId == worker.SessionId.ToString("D")));
            }
            var retentionStore = (ITenantSessionStore)f.Iam;
            var dueWorker = Assert.Single(await retentionStore.ListDueSessionsAsync(null, 100), t => t.Owner.SessionId == worker.SessionId);
            Assert.Equal(dueWorker, await retentionStore.FindExpiredSessionAsync(tenant, worker.SessionId));
            Assert.False(await retentionStore.RetireDueSessionAsync(dueWorker with { Revision = dueWorker.Revision + 1 }, f.Ids.NewId()));
            Assert.True(await retentionStore.RetireDueSessionAsync(dueWorker, f.Ids.NewId()));
            Assert.False(await retentionStore.RetireDueSessionAsync(dueWorker, f.Ids.NewId()));
            Assert.DoesNotContain(await retentionStore.ListDueSessionsAsync(null, 100), t => t.Owner.SessionId == worker.SessionId);
            if (sqlite)
            {
                await using var retired = f.Contexts!.CreateDbContext();
                var saved = await retired.Sessions.SingleAsync(s => s.SessionId == worker.SessionId.ToString("D"));
                Assert.NotNull(saved.DurablyDeletedAtUtc);
                var minimized = await retired.AgentRuns.SingleAsync(r => r.AgentRunId == worker.AgentRunId.ToString("D"));
                Assert.DoesNotContain("Synthetic worker completed", minimized.PayloadJson);
                using var payload = JsonDocument.Parse(minimized.PayloadJson);
                Assert.Equal(AgentRun.RetiredConversationText, payload.RootElement.GetProperty("admission").GetProperty("taskIntent").GetProperty("text").GetString());
                Assert.All(await retired.Entries.Where(e => e.SessionId == worker.SessionId.ToString("D")).ToArrayAsync(), entry => Assert.Empty(entry.Text));
            }
        }
    }

    [Fact]
    public void Tasks_and_messages_are_never_implicitly_added_to_ordinary_Agent_roles()
    {
        var roles = BuiltinAccessRoles.ForTenant(Guid.NewGuid(), Guid.NewGuid);
        Assert.All(roles.Where(r => !r.HumanOnly), role => Assert.DoesNotContain(role.Permissions, p => p.StartsWith("tasks.") || p.StartsWith("agent-messages.")));
        Assert.Contains("tasks.delegate", roles.Single(r => r.Key == BuiltinAccessRoles.Owner).Permissions);
    }
}
