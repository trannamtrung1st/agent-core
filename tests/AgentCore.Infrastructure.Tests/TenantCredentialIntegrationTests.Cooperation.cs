using AgentCore.Application.Ports;
using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Sessions;
using AgentCore.Application.Events;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static async Task<AgentRun> ExecuteCooperation(Fixture f, ServiceProvider services, Guid tenant, Guid runId, GlobalModelProbe probe, AgentRunStatus expected = AgentRunStatus.Completed,
        Func<AgentRun, Task>? beforeDispatch = null)
    {
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, runId))!;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        if (beforeDispatch is not null) await beforeDispatch(run);
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, runId, run.Claim!.Generation); var output = new CapturingSessionOutput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame,
            storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(runId, true, deadline.Token));
            if (expected == AgentRunStatus.Completed && run.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome) await output.WaitForAsync(o => o.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token);
            await runtime.WaitUntilIdleAsync(deadline.Token);
        }
        run = (await runs.FindTenantRunAsync(tenant, runId))!;
        Assert.True(run.Status == expected, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}\n{string.Join("\n", probe.Diagnostics)}\n{string.Join("\n", f.DatabaseErrors)}"); return run;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Cooperation_exact_task_review_admits_one_own_worker(bool sqlite) => Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(sqlite, false, false, false, "review");
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Cooperation_model_task_review_continuation_keeps_actual_source_and_one_worker(bool sqlite) => Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(sqlite, false, false, false, "model");
    [Theory] [InlineData(false, "exceptional")] [InlineData(true, "exceptional")]
    [InlineData(false, "compound")] [InlineData(true, "compound")]
    [InlineData(false, "worker-revoked")] [InlineData(true, "worker-revoked")]
    public Task Cooperation_task_consent_keeps_grant_and_worker_rights_separate(bool sqlite, string boundary) => Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(sqlite, false, false, false, boundary);
    [Theory] [InlineData(false, "independent")] [InlineData(true, "independent")]
    [InlineData(false, "reviewer-revoked")] [InlineData(true, "reviewer-revoked")]
    public Task Cooperation_task_consent_rechecks_current_independent_reviewer(bool sqlite, string boundary) => Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(sqlite, false, false, false, boundary);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Cooperation_model_task_exception_is_exact_and_does_not_add_raw_permissions(bool sqlite) => Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(sqlite, false, false, false, "model-exceptional");
    [Theory] [InlineData(false, false, false, false, null)] [InlineData(true, false, false, false, null)] [InlineData(false, true, false, false, null)] [InlineData(true, true, false, false, null)]
    [InlineData(false, false, true, false, null)] [InlineData(true, false, true, false, null)]
    [InlineData(false, false, false, true, null)] [InlineData(true, false, false, true, null)]
    public Task Cooperation_actual_model_delegates_or_sends_as_its_claimed_Agent(bool sqlite, bool sameAgent, bool direct, bool fanout, string? review)
        => CooperationFlow(sqlite, sameAgent, direct, fanout, review);
    [Theory] [InlineData(false, "scheduled")] [InlineData(true, "scheduled")] [InlineData(false, "manual")] [InlineData(true, "manual")]
    public Task Cooperation_independent_Automation_delegates_with_actual_requester_and_own_worker(bool sqlite, string origin)
        => CooperationFlow(sqlite, false, false, false, null, origin);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Cooperation_three_real_delegation_edges_share_one_frozen_root_budget(bool sqlite)
        => CooperationFlow(sqlite, false, false, false, null, chain: true);
    private async Task CooperationFlow(bool sqlite, bool sameAgent, bool direct, bool fanout, string? review, string? independentOrigin = null, bool chain = false)
    {
        var directClock = direct || independentOrigin is not null ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var f = await Fixture.Create(sqlite, clock: directClock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Cooperation", "cooperation");
        var humanRef = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(humanRef.AccountId, humanRef.AuthenticationSessionId, humanRef.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(human.PrincipalId, tenant, "Cooperative Agent", await Revision(), initiator: human);
        draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { ExecutionBudgets = chain ? new(new(144, 900), new(144, 900), new(144, 900)) : draft.Candidate.ExecutionBudgets, TriggerPolicy = independentOrigin is null ? draft.Candidate.TriggerPolicy : (await f.BuiltIns.GetAsync("general-assistant", 35))!.TriggerPolicy, ExecutionPolicy = new(Capabilities: new("Selected",
            ["tasks.delegate", "tasks.list", "tasks.inspect", "tasks.messages.send", "tasks.messages.list", "tasks.cancel", "tasks.acknowledge", "agent-messages.send", "agent-messages.list", "execution.wait"])) }, await Revision(), draft.Revision, initiator: human);
        await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
        var source = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        var target = sameAgent ? source : await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Explicit cooperation", new[] { "automations.run", "agents.read", "agents.chat", "sessions.read", "sessions.create", "agent-runs.read", "agent-runs.create", "tasks.read", "tasks.delegate", "tasks.cancel", "agent-messages.read", "agent-messages.send" }.Where(key => review is not ("exceptional" or "model-exceptional") || key != "tasks.delegate").ToArray(), await Revision());
        foreach (var agent in new[] { source, target }.DistinctBy(a => a.PrincipalId))
        {
            await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
            await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
            await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, new(AcceptTasks: true, AcceptDirectMessages: true, MaxConcurrentTasks: 8), await Revision(), agent.Instance.Revision, initiator: human);
        }
        source = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, source.Instance.InstanceId))!;
        var session = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, human, SessionMode.Text, await Revision(), source.Instance.Revision);
        Guid submittedId;
        if (independentOrigin is null)
            submittedId = (await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(),
                $"synthetic-{(direct ? "direct-message" : fanout ? "task-fanout" : "task-delegate")}:{target.Instance.InstanceId:D}{(review is "model" or "model-exceptional" ? " review-task" : chain ? " chain-task" : !direct && !fanout ? " report-task-result" : "")}", await Revision(), 1)).RunId;
        else
        {
            var automationOwner = services.GetRequiredService<TenantAutomationService>();
            var automation = await automationOwner.SaveAsync(tenant, source.Instance.InstanceId, null, human,
                new(true, "Independent Task source", $"synthetic-task-delegate:{target.Instance.InstanceId:D}",
                    new AgentCore.Domain.Triggers.OneShotSchedule(f.Clock.GetUtcNow().AddMinutes(1), "UTC")), await Revision(), source.Instance.Revision, 0);
            if (independentOrigin == "scheduled")
            {
                directClock!.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(1, await automationOwner.AdmitDueAsync());
                submittedId = Assert.Single(await ((ITenantRuntimeStore)f.Iam).ListRunnableTenantRunsAsync(f.Clock.GetUtcNow(), 100)).AgentRunId;
            }
            else submittedId = (await automationOwner.RunAsync(tenant, automation.AutomationId, human, await Revision(), source.Instance.Revision, automation.Revision)).RunId!.Value;
        }
        if (review is not null)
        {
            await services.GetRequiredService<TenantOperationPolicyService>().SaveAsync(tenant, human, review == "compound" ? "sessions.create" : "tasks.delegate",
                new(OperationApprovalMode.Always, review == "independent"), await Revision(), 0);
            if (review is "model" or "model-exceptional")
            {
                var paused = await ExecuteCooperation(f, services, tenant, submittedId, probe, AgentRunStatus.WaitingForApproval);
                var approvalOwner = services.GetRequiredService<TenantApprovalService>();
                var exactRequest = (await ((ITenantApprovalStore)f.Iam).FindTaskDelegationApprovalAsync(tenant, $"run:{submittedId:D}", "synthetic-reviewed-delegation"))!;
                Assert.NotNull(exactRequest);
                Assert.Equal(review == "model-exceptional" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, exactRequest.Kind);
                var exact = exactRequest.Operation.TaskDelegation!.Task;
                Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, exact.TaskId));
                Assert.True((await approvalOwner.ReadViewAsync(tenant, exactRequest.RequestId, human)).Review!.CanDecide);
                var listed = await approvalOwner.ListAsync(tenant, human, new());
                Assert.Equal(exactRequest.RequestId, Assert.Single(listed.Items).Request.RequestId); Assert.Equal(1, listed.Total);
                await approvalOwner.DecideAsync(tenant, exactRequest.RequestId, human, true, exactRequest.Operation.ActionHash, await Revision(), exactRequest.Revision);
                await approvalOwner.DispatchPendingAsync();
                var original = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, submittedId))!;
                Assert.Equal(AgentRunOutcomeKind.Continued, original.Result!.OutcomeKind);
                var successor = await ExecuteCooperation(f, services, tenant, original.Result.ContinuationRunId!.Value, probe);
                if (review == "model-exceptional")
                {
                    Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(tenant, exactRequest.RequestId))!.Status);
                    Assert.False((await f.Authorizer.CheckOwnedTenantAsync((await f.Iam.ReadTenantAsync(tenant))!, source.PrincipalId, PermissionCatalog.Get("tasks.delegate"), TenantSessionRules.Scope(tenant, session.Owner.SessionId), default)).Allowed);
                }
                var savedTask = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, exact.TaskId))!;
                Assert.Equal(original.AgentRunId, savedTask.Source.RunId); Assert.NotEqual(successor.AgentRunId, savedTask.Source.RunId);
                await ExecuteCooperation(f, services, tenant, savedTask.InitialWorkerRunId!.Value, probe);
                await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
                Assert.Equal(DelegatedTaskState.Succeeded, (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, savedTask.TaskId))!.State);
                return;
            }
            var decidingHuman = human; Guid? reviewerRoleId = null;
            if (review is "independent" or "reviewer-revoked")
            {
                var account = await f.Identity.RegisterAsync("task-reviewer@example.test", "Task reviewer", "strong-reviewer-password");
                var reviewerRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Exact Task review", ["models.read", "approvals.read", "approvals.approve", "agents.read", "sessions.read", "spaces.read", "environments.read", "tasks.delegate", "sessions.create", "agent-runs.create"], await Revision());
                reviewerRoleId = reviewerRole.RoleId;
                var invite = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, reviewerRole.RoleId, await Revision());
                var reviewerUser = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
                var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
                decidingHuman = new(account.AccountId, login.SessionId, reviewerUser.PrincipalId);
            }
            var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
            var claimed = (await runs.FindTenantRunAsync(tenant, submittedId))!;
            claimed = await storage.ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId,
                new AgentRunCommand.Claim(claimed.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            var approvals = services.GetRequiredService<TenantApprovalService>();
            var arguments = System.Text.Json.JsonSerializer.Serialize(new { objective = "synthetic-task-worker Complete reviewed assignment", target = new { instanceId = target.Instance.InstanceId }, requestKey = "reviewed-assignment" });
            TenantApprovalPreparationResult pending;
            try { pending = await approvals.PrepareTaskDelegationAsync(new(tenant, claimed.AgentRunId), arguments, claimed.Revision, claimed.Claim!.Generation); }
            catch (Exception error) { throw new InvalidOperationException(string.Join("\n", f.DatabaseErrors), error); }
            Assert.Equal(OperationDecisionKind.RequiresApproval, pending.Decision);
            var request = Assert.IsType<TenantApprovalRequest>(pending.Request);
            Assert.Equal(review == "exceptional" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            var prepared = request.Operation.TaskDelegation!.Task;
            Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, prepared.TaskId));
            Assert.Null(await runs.FindTenantRunAsync(tenant, prepared.InitialWorkerRunId!.Value));
            Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, claimed.AgentRunId))!.Status);
            if (review == "independent")
            {
                Assert.False((await approvals.ReadViewAsync(tenant, request.RequestId, human)).Review!.CanDecide);
                var decisionRevision = await Revision();
                await Assert.ThrowsAsync<AccessDeniedException>(() => approvals.DecideAsync(tenant, request.RequestId, human, true, request.Operation.ActionHash, decisionRevision, request.Revision).AsTask());
            }
            var view = await approvals.ReadViewAsync(tenant, request.RequestId, decidingHuman);
            Assert.True(view.Review!.CanDecide);
            await approvals.DecideAsync(tenant, request.RequestId, decidingHuman, true, request.Operation.ActionHash, await Revision(), request.Revision);
            await approvals.DispatchPendingAsync();
            var resumed = (await runs.FindTenantRunAsync(tenant, claimed.AgentRunId))!;
            resumed = await storage.ApplyTenantRuntimeCommandAsync(resumed.Owner, resumed.AgentRunId,
                new AgentRunCommand.Claim(resumed.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            if (review == "worker-revoked")
            {
                var current = (await f.Iam.ReadTenantAsync(tenant))!;
                var targetAssignment = current.Assignments.Single(a => a.RoleId == role.RoleId && a.PrincipalId == target.PrincipalId);
                await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, targetAssignment.AssignmentId, current.Tenant.Revision);
            }
            if (review == "reviewer-revoked")
            {
                var current = (await f.Iam.ReadTenantAsync(tenant))!;
                var reviewerAssignment = current.Assignments.Single(a => a.RoleId == reviewerRoleId && a.PrincipalId == decidingHuman.PrincipalId);
                await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, reviewerAssignment.AssignmentId, current.Tenant.Revision);
            }
            var effects = services.GetRequiredService<TenantApprovedOperationService>();
            var receipt = await effects.ExecuteAsync(new(tenant, resumed.AgentRunId), request.RequestId, request.Operation.OperationId,
                resumed.Claim!.Generation, request.Operation.ActionHash);
            if (review is "worker-revoked" or "reviewer-revoked")
            {
                Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Null(receipt.TaskAdmission);
                Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, prepared.TaskId));
                Assert.Null(await runs.FindTenantRunAsync(tenant, prepared.InitialWorkerRunId!.Value));
                Assert.Equal(receipt, await effects.ExecuteAsync(new(tenant, resumed.AgentRunId), request.RequestId, request.Operation.OperationId,
                    resumed.Claim!.Generation, request.Operation.ActionHash));
                return;
            }
            Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal("task_created", receipt.ReasonCode);
            Assert.Equal(prepared.TaskId, receipt.TaskAdmission!.TaskId);
            Assert.Equal(receipt, await effects.ExecuteAsync(new(tenant, resumed.AgentRunId), request.RequestId, request.Operation.OperationId,
                resumed.Claim.Generation, request.Operation.ActionHash));
            if (review == "exceptional")
            {
                Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(tenant, request.RequestId))!.Status);
                Assert.False((await f.Authorizer.CheckOwnedTenantAsync((await f.Iam.ReadTenantAsync(tenant))!, source.PrincipalId, PermissionCatalog.Get("tasks.delegate"), TenantSessionRules.Scope(tenant, session.Owner.SessionId), default)).Allowed);
            }
            if (sqlite)
            {
                await using var db = f.Contexts!.CreateDbContext();
                Assert.False(db.Database.HasPendingModelChanges());
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET TaskAdmissionJson=json_set(TaskAdmissionJson,'$.TaskId',{f.Ids.NewId().ToString("D")}) WHERE OperationId={receipt.OperationId}"));
                Assert.Equal(receipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, receipt.OperationId));
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.TaskDelegation.Task.Request.Objective','Substituted after consent') WHERE RequestId={request.RequestId}"));
            }
            var reviewedTask = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, prepared.TaskId))!;
            var reviewedWorker = await ExecuteCooperation(f, services, tenant, reviewedTask.InitialWorkerRunId!.Value, probe);
            Assert.Equal(target.PrincipalId, reviewedWorker.Owner.ExecutorPrincipalId);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            Assert.Equal(DelegatedTaskState.Succeeded, (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, reviewedTask.TaskId))!.State);
            return;
        }
        var sourceRun = await ExecuteCooperation(f, services, tenant, submittedId, probe, fanout ? AgentRunStatus.WaitingForSignal : AgentRunStatus.Completed,
            beforeDispatch: direct || fanout || independentOrigin is not null ? null : async claimed => {
                var proposed = await services.GetRequiredService<TenantApprovalService>().PrepareTaskDelegationAsync(new(tenant, claimed.AgentRunId),
                    System.Text.Json.JsonSerializer.Serialize(new { objective = "synthetic-task-worker Prepare exact assignment only", target = new { instanceId = target.Instance.InstanceId }, requestKey = "consent-preparation-only" }),
                    claimed.Revision, claimed.Claim!.Generation);
                Assert.Equal(OperationDecisionKind.Allowed, proposed.Decision); Assert.Null(proposed.Request);
                var intent = proposed.Operation.TaskDelegation!;
                Assert.Equal(claimed.AgentRunId, intent.Task.Source.RunId); Assert.Equal(claimed.Claim.Generation, intent.SourceGeneration);
                Assert.Equal(target.PrincipalId, intent.Task.Assignment.PrincipalId);
                Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, intent.Task.TaskId));
                Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, intent.Task.WorkerSessionId!.Value));
                Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, intent.Task.InitialWorkerRunId!.Value));
                var approval = new TenantApprovalRequest(f.Ids.NewId(), tenant, claimed.AgentRunId, claimed.SessionId, claimed.Admission.Activation.RootActivationId,
                    claimed.Owner.ExecutorPrincipalId!.Value, human.PrincipalId, ApprovalKind.Review, proposed.Operation, TenantApprovalStatus.Pending, 1,
                    f.Clock.GetUtcNow(), f.Clock.GetUtcNow().AddHours(1), new(source.Instance.Persona.Name));
                var binding = await services.GetRequiredService<TenantTaskDelegationTarget>().RequireAsync(approval, claimed, (await f.Iam.ReadTenantAsync(tenant))!, default);
                var graph = TenantTaskDelegationTarget.Build(approval, binding, await Revision(), f.Ids.NewId());
                TenantTaskConsentRules.MatchAdmission(approval, graph);
                Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, graph.WorkerRun!.AgentRunId));
            });
        if (independentOrigin is not null)
        {
            var root = sourceRun.Admission.ExecutionContext!;
            Assert.Equal(independentOrigin == "scheduled" ? (Guid?)null : human.PrincipalId, root.OriginalInitiatorPrincipalId);
            Assert.Null(root.ParentRunId);
            var independentTask = Assert.Single((await ((ITenantTaskStore)f.Iam).ReadTasksAsync(new(tenant, new(Human: human), SessionId: sourceRun.SessionId))).Items);
            Assert.Equal(sourceRun.AgentRunId, independentTask.Source.RunId);
            Assert.Equal(root.OriginalInitiatorPrincipalId, independentTask.Source.OriginalInitiatorPrincipalId);
            Assert.Equal(root.RootActivationId, independentTask.Source.RootActivationId);
            var independentWorker = await ExecuteCooperation(f, services, tenant, independentTask.InitialWorkerRunId!.Value, probe);
            Assert.Equal(target.PrincipalId, independentWorker.Owner.ExecutorPrincipalId);
            Assert.Equal(root.OriginalInitiatorPrincipalId, independentWorker.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            Assert.Equal(DelegatedTaskState.Succeeded, (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, independentTask.TaskId))!.State);
            return;
        }
        if (direct)
        {
            var inbox = (ITenantAgentMessageStore)f.Iam; var actor = new TaskActor(Human: human);
            var thread = Assert.Single((await inbox.ReadAgentThreadsAsync(new(tenant, actor, InstanceId: source.Instance.InstanceId, Scope: "direct"))).Items);
            var outsider = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
            await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, outsider.PrincipalId);
            await f.Access.AssignRoleAsync(human.PrincipalId, tenant, outsider.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
            var outsiderSession = await f.Sessions.CreateAsync(tenant, outsider.Instance.InstanceId, human, SessionMode.Text, await Revision(), outsider.Instance.Revision);
            var outsiderCommand = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, outsiderSession.Owner.SessionId, human,
                f.Ids.NewId(), "Inspect only authorized correspondence", await Revision(), 1);
            var outsiderRun = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, outsiderCommand.RunId))!;
            outsiderRun = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(outsiderRun.Owner, outsiderRun.AgentRunId,
                new AgentRunCommand.Claim(outsiderRun.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            var outsiderActor = new TaskActor(Agent: new(outsiderRun.Owner, outsiderRun.SessionId, outsiderRun.AgentRunId, outsiderRun.Claim!.Generation), Tool: "agent-messages.list");
            Assert.Empty((await inbox.ReadAgentThreadsAsync(new(tenant, outsiderActor, Scope: "direct"))).Items);
            await Assert.ThrowsAsync<AccessDeniedException>(() => inbox.ReadAgentMessagesAsync(tenant, outsiderActor, thread.ThreadId, 0, 50).AsTask());
            var message = Assert.Single((await inbox.ReadAgentMessagesAsync(tenant, actor, thread.ThreadId, 0, 50)).Items);
            Assert.Equal(source.PrincipalId, message.SenderPrincipalId); Assert.Equal(sourceRun.AgentRunId, message.SourceRunId);
            var receipt = (await inbox.FindAgentMessageSourceAsync(tenant, message.MessageId))!.Receipt;
            Assert.Equal(AgentMessageDelivery.DeliveredToAuthorizedInbox, receipt.State); Assert.Null(receipt.WakeRunId);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            Assert.Empty(await inbox.ListAgentMessageWakesAsync(32));
            target = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, target.Instance.InstanceId))!;
            target = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, target.Instance.InstanceId,
                new(AcceptTasks: true, AcceptDirectMessages: true, AllowDirectMessageWake: true), await Revision(), target.Instance.Revision, initiator: human);
            source = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, source.Instance.InstanceId,
                new(AcceptTasks: true, AcceptDirectMessages: true, AllowDirectMessageWake: true), await Revision(), source.Instance.Revision, initiator: human);
            var secondSession = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, human, SessionMode.Text, await Revision(), source.Instance.Revision, retentionKind: SessionRetentionKind.Temporary);
            var second = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, secondSession.Owner.SessionId, human, f.Ids.NewId(),
                $"synthetic-direct-message:{target.Instance.InstanceId:D} bounce:{source.Instance.InstanceId:D}:{target.Instance.InstanceId:D}", await Revision(), 1);
            await ExecuteCooperation(f, services, tenant, second.RunId, probe);
            var newest = (await inbox.ReadAgentMessagesAsync(tenant, actor, thread.ThreadId, message.Sequence, 50)).Items.Single();
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            var delivered = (await inbox.FindAgentMessageSourceAsync(tenant, newest.MessageId))!.Receipt;
            Assert.True(delivered.State == AgentMessageDelivery.ActivationAdmitted, delivered.State + ": " + delivered.Reason); Assert.Equal(1, delivered.WakeHops);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            Assert.Equal(delivered.WakeRunId, (await inbox.FindAgentMessageSourceAsync(tenant, newest.MessageId))!.Receipt.WakeRunId);
            var wake = await ExecuteCooperation(f, services, tenant, delivered.WakeRunId!.Value, probe);
            Assert.Equal(target.PrincipalId, wake.Owner.ExecutorPrincipalId); Assert.Equal(target.Instance.InstanceId, wake.AgentInstanceId);
            Assert.NotEqual(secondSession.Owner.SessionId, wake.SessionId); Assert.Equal(wake.AgentRunId, (await TenantExecutionLineage.RootAsync(wake, (ITenantRunStore)f.Iam, default)).AgentRunId);
            Assert.Contains("authorized inbox", wake.Result!.Text); Assert.Empty(http.Requests);
            if (sqlite)
            {
                var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                Assert.Equal(wake.AgentRunId, (await ((ITenantAgentMessageStore)reopened).FindAgentMessageSourceAsync(tenant, newest.MessageId))!.Receipt.WakeRunId);
            }
            // The actual recipient replies through its own claimed Run. A restart
            // cannot turn that reply into a new independent wake allowance.
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            var reply = Assert.Single((await inbox.ReadAgentMessagesAsync(tenant, actor, thread.ThreadId, newest.Sequence, 50)).Items);
            var replyReceipt = (await inbox.FindAgentMessageSourceAsync(tenant, reply.MessageId))!.Receipt;
            Assert.Equal(2, replyReceipt.WakeHops); Assert.Equal(delivered.WakeRootActivationId, replyReceipt.WakeRootActivationId);
            var bounceRun = await ExecuteCooperation(f, services, tenant, replyReceipt.WakeRunId!.Value, probe);
            Assert.Equal(source.PrincipalId, bounceRun.Owner.ExecutorPrincipalId);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            var refusedBounce = Assert.Single((await inbox.ReadAgentMessagesAsync(tenant, actor, thread.ThreadId, reply.Sequence, 50)).Items);
            var refusedReceipt = (await inbox.FindAgentMessageSourceAsync(tenant, refusedBounce.MessageId))!.Receipt;
            Assert.Equal(AgentMessageDelivery.Undeliverable, refusedReceipt.State); Assert.Equal("message_wake_hop_limit", refusedReceipt.Reason); Assert.Null(refusedReceipt.WakeRunId);
            var lastSequence = refusedBounce.Sequence; Guid? independentWakeId = null;
            // Two actual admissions above plus six independent roots fill the pair's
            // persisted hourly allowance. The ninth message remains factual inbox data.
            for (var index = 0; index < 7; index++)
            {
                var anotherSession = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, human, SessionMode.Text, await Revision(), source.Instance.Revision);
                var sent = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, anotherSession.Owner.SessionId, human, f.Ids.NewId(),
                    $"synthetic-direct-message:{target.Instance.InstanceId:D}", await Revision(), 1);
                await ExecuteCooperation(f, services, tenant, sent.RunId, probe);
                var item = Assert.Single((await inbox.ReadAgentMessagesAsync(tenant, actor, thread.ThreadId, lastSequence, 50)).Items); lastSequence = item.Sequence;
                await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
                var savedWake = (await inbox.FindAgentMessageSourceAsync(tenant, item.MessageId))!.Receipt;
                if (index < 6)
                {
                    Assert.Equal(AgentMessageDelivery.ActivationAdmitted, savedWake.State); Assert.Equal(1, savedWake.WakeHops);
                    await ExecuteCooperation(f, services, tenant, savedWake.WakeRunId!.Value, probe); independentWakeId = savedWake.WakeRunId;
                }
                else
                {
                    Assert.Equal(AgentMessageDelivery.Undeliverable, savedWake.State); Assert.Equal("message_pair_rate_limit", savedWake.Reason); Assert.Null(savedWake.WakeRunId);
                    if (sqlite)
                    {
                        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                        Assert.Equal(savedWake, (await ((ITenantAgentMessageStore)reopened).FindAgentMessageSourceAsync(tenant, item.MessageId))!.Receipt);
                    }
                }
            }
            directClock!.Advance(TimeSpan.FromHours(24));
            Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, wake.SessionId));
            Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, wake.AgentRunId));
            Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, bounceRun.SessionId));
            Assert.NotNull(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, independentWakeId!.Value));
            var freshReader = new TaskActor(Human: await SandboxHuman(f, owner.OwnerPrincipalId));
            var retainedPage = await inbox.ReadAgentMessagesAsync(tenant, freshReader, thread.ThreadId, 0, 50);
            Assert.DoesNotContain(retainedPage.Items, m => m.MessageId == newest.MessageId || m.MessageId == reply.MessageId || m.MessageId == refusedBounce.MessageId);
            Assert.Equal(8, retainedPage.Items.Count);
            var currentHuman = new TenantSessionInitiator(freshReader.Human!.AccountId, freshReader.Human.AuthenticationSessionId, freshReader.Human.PrincipalId);
            var sessions = (ITenantSessionStore)f.Iam;
            foreach (var sourceSessionId in retainedPage.Items.Select(m => m.SourceSessionId!.Value).Distinct())
            {
                var sourceSession = (await sessions.FindTenantSessionAsync(tenant, sourceSessionId))!;
                await f.Sessions.ChangeAsync(tenant, currentHuman, new(sourceSessionId, sourceSession.Snapshot.Revision, TenantSessionAction.Delete), await Revision());
            }
            Assert.Empty((await inbox.ReadAgentThreadsAsync(new(tenant, freshReader, InstanceId: source.Instance.InstanceId, Scope: "direct"))).Items);
            await Assert.ThrowsAsync<AccessDeniedException>(() => inbox.ReadAgentMessagesAsync(tenant, freshReader, thread.ThreadId, 0, 50).AsTask());
            var wakeRetirement = await sessions.FindExpiredSessionAsync(tenant, wake.SessionId); Assert.NotNull(wakeRetirement);
            Assert.True(await sessions.RetireDueSessionAsync(wakeRetirement, f.Ids.NewId()));
            Assert.False(await sessions.RetireDueSessionAsync(wakeRetirement, f.Ids.NewId()));
            if (sqlite)
            {
                await using var retired = f.Contexts!.CreateDbContext();
                var minimized = await retired.AgentRuns.SingleAsync(r => r.AgentRunId == wake.AgentRunId.ToString("D"));
                using var payload = System.Text.Json.JsonDocument.Parse(minimized.PayloadJson);
                Assert.Equal(AgentRun.RetiredConversationText, payload.RootElement.GetProperty("admission").GetProperty("taskIntent").GetProperty("text").GetString());
            }
            // A later authorized conversation starts a new generation. The old
            // exact thread ID cannot be used to read this newly retained source.
            var freshSource = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, currentHuman, SessionMode.Text, await Revision(), source.Instance.Revision);
            var freshCommand = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, freshSource.Owner.SessionId, currentHuman,
                f.Ids.NewId(), $"synthetic-direct-message:{target.Instance.InstanceId:D}", await Revision(), 1);
            await ExecuteCooperation(f, services, tenant, freshCommand.RunId, probe);
            var generation = Assert.Single((await inbox.ReadAgentThreadsAsync(new(tenant, freshReader, InstanceId: source.Instance.InstanceId, Scope: "direct"))).Items);
            Assert.NotEqual(thread.ThreadId, generation.ThreadId);
            var freshMessage = Assert.Single((await inbox.ReadAgentMessagesAsync(tenant, freshReader, generation.ThreadId, 0, 50)).Items);
            Assert.Equal(1, freshMessage.Sequence);
            await Assert.ThrowsAsync<AccessDeniedException>(() => inbox.ReadAgentMessagesAsync(tenant, freshReader, thread.ThreadId, 0, 50).AsTask());
            if (sqlite)
            {
                var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantAgentMessageStore)reopened).ReadAgentMessagesAsync(tenant, freshReader, thread.ThreadId, 0, 50).AsTask());
                Assert.Equal(generation.ThreadId, Assert.Single((await ((ITenantAgentMessageStore)reopened).ReadAgentThreadsAsync(new(tenant, freshReader, Scope: "direct"))).Items).ThreadId);
            }
            Assert.Empty(http.Requests); return;
        }
        var store = (ITenantTaskStore)f.Iam;
        if (fanout)
        {
            var children = (await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: session.Owner.SessionId))).Items;
            Assert.Equal(2, children.Count); Assert.Equal(2, sourceRun.Wait!.Tasks!.Count);
            var reserved = await store.ReadTaskExecutionAllowanceAsync(sourceRun); Assert.NotNull(reserved);
            Assert.Equal(24, reserved.MaxSteps + children.Sum(t => t.Assignment.Budget!.MaxSteps));
            Assert.Equal(180, reserved.DurationSeconds + children.Sum(t => t.Assignment.Budget!.DurationSeconds));
            if (sqlite)
            {
                var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                Assert.Equal(reserved, await ((ITenantTaskStore)reopened).ReadTaskExecutionAllowanceAsync(sourceRun));
            }
            foreach (var child in children)
            {
                var childRun = await ExecuteCooperation(f, services, tenant, child.InitialWorkerRunId!.Value, probe);
                Assert.Equal(child.Assignment.Budget!.MaxSteps, childRun.Admission.ExecutionBudget!.Profile.MaxSteps);
            }
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            var released = await store.ReadTaskExecutionAllowanceAsync(sourceRun); Assert.True(released!.MaxSteps > reserved.MaxSteps);
            var storage = (ITenantRuntimeStore)f.Iam;
            sourceRun = await storage.ApplyTenantRuntimeCommandAsync(sourceRun.Owner, sourceRun.AgentRunId,
                new AgentRunCommand.ResumeWait(sourceRun.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            // ResumeWait already owns the live claim; dispatch directly instead of acquiring another attempt.
            var frame = new TenantRuntimeFrame(sourceRun.Owner, sourceRun.SessionId, sourceRun.AgentRunId, sourceRun.Claim!.Generation);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = new CapturingSessionOutput();
            await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame,
                storage, (ITenantRunStore)f.Iam, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
            {
                Assert.True(await runtime.DispatchAgentRunAsync(sourceRun.AgentRunId, true, deadline.Token));
                await output.WaitForAsync(o => o.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token);
            }
            var final = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, sourceRun.AgentRunId))!;
            Assert.True(final.Status == AgentRunStatus.Completed, final.Failure?.Code + "\n" + string.Join("\n", probe.Diagnostics));
            Assert.Equal(1, final.AttemptCount); Assert.Equal(1, final.WaitCount);
            Assert.Equal(2, (await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: session.Owner.SessionId))).Items.Count); Assert.Empty(http.Requests); return;
        }
        if (chain)
        {
            var parentSession = sourceRun.SessionId;
            Guid? parentTaskId = null;
            for (var depth = 1; depth <= 3; depth++)
            {
                var nestedTask = Assert.Single((await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: parentSession))).Items);
                Assert.Equal(depth, nestedTask.Source.Depth); Assert.Equal(parentTaskId, nestedTask.Source.ParentTaskId);
                Assert.Equal(sourceRun.ActivationId, nestedTask.Source.RootActivationId);
                Assert.Equal(144, nestedTask.Assignment.Budget!.RootMaxSteps); Assert.Equal(900, nestedTask.Assignment.Budget.RootDurationSeconds);
                var nestedWorker = await ExecuteCooperation(f, services, tenant, nestedTask.InitialWorkerRunId!.Value, probe);
                Assert.Equal(target.PrincipalId, nestedWorker.Owner.ExecutorPrincipalId);
                Assert.Equal(nestedTask.Assignment.Budget.MaxSteps, nestedWorker.Admission.ExecutionBudget!.Profile.MaxSteps);
                Assert.True(nestedWorker.Admission.ExecutionBudget.Profile.MaxSteps < 144);
                parentSession = nestedWorker.SessionId; parentTaskId = nestedTask.TaskId;
            }
            Assert.Empty((await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: parentSession))).Items);
            await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
            if (sqlite)
            {
                var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                var retained = (await ((ITenantTaskStore)reopened).FindTaskAsync(tenant, parentTaskId!.Value))!;
                Assert.Equal(3, retained.Source.Depth); Assert.Equal(144, retained.Assignment.Budget!.RootMaxSteps);
            }
            Assert.Empty(http.Requests); return;
        }
        var task = Assert.Single((await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: session.Owner.SessionId))).Items);
        Assert.Equal(sourceRun.AgentRunId, task.Source.RunId); Assert.Equal(source.PrincipalId, task.Source.PrincipalId); Assert.Equal(sourceRun.ActivationId, task.Source.ActivationId);
        Assert.Equal(sourceRun.Admission.Activation.RootActivationId, task.Source.RootActivationId); Assert.NotEqual(session.Owner.SessionId, task.WorkerSessionId);
        var worker = await ExecuteCooperation(f, services, tenant, task.InitialWorkerRunId!.Value, probe);
        Assert.Equal(target.PrincipalId, worker.Owner.ExecutorPrincipalId); Assert.Equal(sourceRun.AgentRunId, (await TenantExecutionLineage.RootAsync(worker, (ITenantRunStore)f.Iam, default)).AgentRunId);
        await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
        Assert.Equal(DelegatedTaskState.Succeeded, (await store.FindTaskAsync(tenant, task.TaskId))!.State);
        var reportNotice = Assert.Single((await store.ReadTaskNotificationsAsync(tenant, new(Human: human), task.TaskId)).Items);
        Assert.Null(reportNotice.WithheldReason); Assert.NotNull(reportNotice.HandlingRunId);
        var report = await ExecuteCooperation(f, services, tenant, reportNotice.HandlingRunId.Value, probe, beforeDispatch: async claimed => {
            Assert.Equal(8, claimed.Admission.ExecutionBudget!.Profile.MaxSteps); Assert.Equal(60, claimed.Admission.ExecutionBudget.Profile.DurationSeconds);
            Assert.DoesNotContain("tasks.delegate", await services.GetRequiredService<TenantTaskToolService>().OfferedAsync(claimed, default));
            var refused = await Assert.ThrowsAsync<AccessDeniedException>(() => services.GetRequiredService<TenantTaskToolService>().ExecuteAsync(
                new(claimed.Owner, claimed.SessionId, claimed.AgentRunId, claimed.Claim!.Generation),
                new("no-report-redelegation", "tasks.delegate", System.Text.Json.JsonSerializer.Serialize(new { objective = "Must not admit", target = new { instanceId = target.Instance.InstanceId }, requestKey = "report-cannot-extend-root" })),
                4096, default).AsTask());
            Assert.Equal("task_report_delegation_unavailable", refused.ReasonCode);
        });
        Assert.Equal(source.PrincipalId, report.Owner.ExecutorPrincipalId); Assert.Equal(task.TaskId, report.Admission.TaskReport!.TaskId);
        Assert.Single((await store.ReadTasksAsync(new(tenant, new(Human: human), SessionId: session.Owner.SessionId))).Items);
        Assert.Empty(http.Requests);
    }
}
