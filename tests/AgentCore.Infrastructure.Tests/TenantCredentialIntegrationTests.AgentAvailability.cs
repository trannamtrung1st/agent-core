using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Disable_blocks_dependency_waiting_Tasks_and_reenable_does_not_admit_old_work(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Availability", "availability");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Worker admission", ["sessions.create", "agent-runs.create", "agent-messages.read", "agents.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        agent = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, agent.Instance.InstanceId,
            new(AcceptTasks: true, MaxConcurrentTasks: 8), await Revision(), agent.Instance.Revision, initiator: human);
        var source = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var service = TaskService(f); var actor = new TaskActor(Human: human);
        var first = (await service.DelegateAsync(tenant, source.Owner.SessionId, actor,
            new("Inspect first source", "first", [agent.Instance.InstanceId], false, null, null, [], false, null, "first"), await Revision())).Task;
        var pending = (await service.DelegateAsync(tenant, source.Owner.SessionId, actor,
            new("Inspect next source", "pending", [agent.Instance.InstanceId], false, null, null, [first.TaskId], false, null, "pending"), await Revision())).Task;
        Assert.Equal(DelegatedTaskState.PendingDependencies, pending.State);
        agent = await f.Agents.SetLifecycleAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, AgentInstanceLifecycle.Disabled,
            await Revision(), agent.Instance.Revision, initiator: human);
        var store = (ITenantTaskStore)f.Iam;
        var blocked = (await store.FindTaskAsync(tenant, pending.TaskId))!;
        Assert.Equal(DelegatedTaskState.Blocked, blocked.State); Assert.Equal("agent_disabled", blocked.Reason); Assert.Null(blocked.InitialWorkerRunId);
        Assert.True(blocked.CancellationRequested);
        Assert.Equal(AgentRunStatus.Cancelled, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, first.InitialWorkerRunId!.Value))!.Status);
        await f.Agents.SetLifecycleAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, AgentInstanceLifecycle.Active,
            await Revision(), agent.Instance.Revision, initiator: human);
        var retained = (await store.FindTaskAsync(tenant, pending.TaskId))!;
        Assert.Equal(blocked.Revision, retained.Revision); Assert.Equal(DelegatedTaskState.Blocked, retained.State);
        Assert.Null(retained.InitialWorkerRunId); Assert.Equal("agent_disabled", retained.Reason);
        Assert.DoesNotContain(await store.ListTaskReconciliationAsync(100), candidate => candidate.Task.TaskId == pending.TaskId);
    }
}
