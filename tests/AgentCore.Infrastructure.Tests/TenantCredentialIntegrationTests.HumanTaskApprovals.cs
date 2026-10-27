using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Goals;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Human_task_review_uses_a_real_command_and_one_future_worker_without_a_source_Run(bool sqlite, bool dependency)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Human Task review", "human-task-review");
        var authenticated = await SandboxHuman(f, owner.OwnerPrincipalId);
        var human = new TenantSessionInitiator(authenticated.AccountId, authenticated.AuthenticationSessionId, authenticated.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var source = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var worker = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, worker.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Own Task execution", ["sessions.create", "agent-runs.create", "sessions.read", "agent-runs.read", "agents.read", "tasks.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, worker.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        worker = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, worker.Instance.InstanceId, new(AcceptTasks: true), await Revision(), worker.Instance.Revision, initiator: human);
        var session = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, human, SessionMode.Text, await Revision(), source.Instance.Revision);
        var goalService = GoalService(f); var goalId = f.Ids.NewId();
        var proposedGoal = (await goalService.MutateAsync(tenant, session.Owner.SessionId, goalId, human,
            new(GoalCommandKind.Create, "independent-goal", await Revision(), 0, 0, HumanGoal()))).Goal;
        var activeGoal = (await goalService.MutateAsync(tenant, session.Owner.SessionId, goalId, human,
            new(GoalCommandKind.PlanDecision, "accept-independent-plan", await Revision(), proposedGoal.Revision, proposedGoal.PlanRevision, Action: "accept"))).Goal;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var approvals = services.GetRequiredService<TenantApprovalService>(); var tasks = (ITenantTaskStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        await services.GetRequiredService<TenantOperationPolicyService>().SaveAsync(tenant, human, "tasks.delegate", new(OperationApprovalMode.Always, false), await Revision(), 0);
        async Task<TenantApprovalRequest> Prepare(string key, Guid[]? dependencies = null)
        {
            try
            {
                var prepared = await approvals.PrepareHumanTaskDelegationAsync(tenant, session.Owner.SessionId, human,
                    new("synthetic-task-worker Complete the approved human assignment", key, [worker.Instance.InstanceId], false, null, null, dependencies ?? [], false, null, key), await Revision());
                Assert.Null(prepared.Task); var request = Assert.IsType<TenantApprovalRequest>(prepared.Approval);
                var intent = request.Operation.TaskDelegation!;
                Assert.Equal(TaskActorKind.Human, intent.Task.Source.ActorKind); Assert.Null(intent.Task.Source.RunId); Assert.Null(intent.Task.Source.ActivationId);
                Assert.NotNull(intent.Task.Source.HumanCommandId); Assert.Equal(human.PrincipalId, request.ExecutorPrincipalId);
                Assert.Null(await tasks.FindTaskAsync(tenant, intent.Task.TaskId)); Assert.Null(await runs.FindTenantRunAsync(tenant, request.RunId));
                var view = await approvals.ReadViewAsync(tenant, request.RequestId, human); Assert.True(view.Review!.CanDecide);
                var page = await approvals.ListAsync(tenant, human, new(SessionId: session.Owner.SessionId)); Assert.Contains(page.Items, item => item.Request.RequestId == request.RequestId);
                if (sqlite)
                {
                    await using var db = f.Contexts!.CreateDbContext();
                    var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == request.RequestId);
                    Assert.Null(row.RunId); Assert.Null(row.SessionId); Assert.False(db.Database.HasPendingModelChanges());
                }
                await approvals.DecideAsync(tenant, request.RequestId, human, true, request.Operation.ActionHash, await Revision(), request.Revision);
                Assert.Equal(1, await approvals.DispatchPendingAsync()); Assert.Equal(0, await approvals.DispatchPendingAsync());
                var outcome = await ((ITenantHumanTaskApprovalStore)f.Iam).FindHumanTaskApprovalOutcomeAsync(tenant, request.Operation.OperationId);
                Assert.True(outcome!.Admitted, outcome.ReasonCode); Assert.Equal(intent.Task.TaskId, outcome.TaskAdmission!.TaskId);
                Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                return request;
            }
            catch (Exception error) { throw new InvalidOperationException(string.Join("\n", f.DatabaseErrors), error); }
        }
        var first = await Prepare("first-human-command"); var request = dependency ? await Prepare("dependent-human-command", [first.Operation.TaskDelegation!.Task.TaskId]) : first;
        if (dependency)
        {
            var waiting = (await tasks.FindTaskAsync(tenant, request.Operation.TaskDelegation!.Task.TaskId))!;
            Assert.Equal(DelegatedTaskState.PendingDependencies, waiting.State); Assert.Null(waiting.InitialWorkerRunId); Assert.Null(waiting.WorkerSessionId);
            Assert.Null(await runs.FindTenantRunAsync(tenant, request.RunId));
            await ExecuteCooperation(f, services, tenant, first.RunId, probe);
            for (var pass = 0; pass < 3; pass++) await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
        }
        var admitted = (await tasks.FindTaskAsync(tenant, request.Operation.TaskDelegation!.Task.TaskId))!;
        Assert.Equal(request.RunId, admitted.InitialWorkerRunId);
        var completed = await ExecuteCooperation(f, services, tenant, admitted.InitialWorkerRunId!.Value, probe);
        Assert.Equal(worker.PrincipalId, completed.Owner.ExecutorPrincipalId); Assert.Null(completed.Admission.ExecutionContext!.ParentRunId);
        Assert.Equal(admitted.Source.RootActivationId, completed.ActivationId);
        await services.GetRequiredService<TenantTaskCoordinationService>().ReconcileAsync(32, default);
        Assert.Equal(DelegatedTaskState.Succeeded, (await tasks.FindTaskAsync(tenant, admitted.TaskId))!.State);
        var stillIndependent = await goalService.ReadAsync(tenant, session.Owner.SessionId, goalId, human);
        Assert.Equal(GoalStatus.Active, stillIndependent.Status); Assert.Equal(activeGoal.Revision, stillIndependent.Revision);
        Assert.All(stillIndependent.Checkpoints, checkpoint => Assert.Equal(GoalCheckpointStatus.Pending, checkpoint.Status));
        Assert.Empty(await runs.ListTenantRunIdsAsync(tenant, session.Owner.SessionId));
        var originalHash = request.Operation.ActionHash;
        var pending = (await approvals.PrepareHumanTaskDelegationAsync(tenant, session.Owner.SessionId, human,
            new("Private objective never admitted", "Pending source retirement", [worker.Instance.InstanceId], false, null, null, [], false, null, "pending-source-retirement"), await Revision())).Approval!;
        Assert.Null(await tasks.FindTaskAsync(tenant, pending.Operation.TaskDelegation!.Task.TaskId));
        Assert.Null(await runs.FindTenantRunAsync(tenant, pending.RunId));
        await f.Sessions.ChangeAsync(tenant, human, new(session.Owner.SessionId, session.Revision, TenantSessionAction.Delete), await Revision());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(tenant, request.RequestId));
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(tenant, pending.RequestId));
        Assert.Null(await ((ITenantHumanTaskApprovalStore)f.Iam).FindHumanTaskApprovalOutcomeAsync(tenant, pending.Operation.OperationId));
        Assert.Null(await runs.FindTenantRunAsync(tenant, pending.RunId));
        Assert.Null(await tasks.FindTaskAsync(tenant, admitted.TaskId));
        Assert.NotNull(await ((ITenantHumanTaskApprovalStore)f.Iam).FindHumanTaskApprovalOutcomeAsync(tenant, request.Operation.OperationId));
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var tombstone = await db.Set<TenantApprovalRecord>().IgnoreQueryFilters().SingleAsync(a => a.RequestId == request.RequestId);
            Assert.NotNull(tombstone.ContentRetiredAt); Assert.Equal(originalHash, tombstone.Fingerprint);
            var pendingTombstone = await db.Set<TenantApprovalRecord>().IgnoreQueryFilters().SingleAsync(a => a.RequestId == pending.RequestId);
            Assert.Equal((int)TenantApprovalStatus.Cancelled, pendingTombstone.Status); Assert.NotNull(pendingTombstone.ContentRetiredAt);
            Assert.DoesNotContain("Private objective never admitted", pendingTombstone.PayloadJson);
            Assert.DoesNotContain("Complete the approved human assignment", tombstone.PayloadJson);
            var row = await db.Set<AgentCore.Infrastructure.Persistence.DelegatedTaskRow>().SingleAsync(t => t.TaskId == admitted.TaskId);
            Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<DelegatedTask>(row.Json)!.ContentRetiredAt);
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Null(await reopened.FindApprovalAsync(tenant, request.RequestId));
            Assert.Equal(originalHash, (await db.Set<TenantApprovalRecord>().IgnoreQueryFilters().SingleAsync(a => a.RequestId == request.RequestId)).Fingerprint);
        }
    }
}
