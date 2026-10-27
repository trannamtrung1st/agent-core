using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantTaskService TaskCreationService(Fixture f) => new(f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam,
        (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), f.Authorizer, f.Ids, f.Clock);
    private static TenantTaskDelegationTarget TaskCreations(Fixture f) => new((ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
        f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), f.Authorizer, f.Clock);
    private static async Task<RoleAssignment> TaskCreationGrant(Fixture f, Environment s, Guid principal, string name, string[] permissions)
    {
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, name, permissions, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, principal, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    }
    private static async Task<(Environment Setup, TenantApprovalService Approvals, TenantApprovalRequest Request, RoleAssignment Sessions)> PrepareTaskCreation(Fixture f)
    {
        var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        var sessions = await TaskCreationGrant(f, s, s.Agent.PrincipalId, "Own Session creation", ["sessions.create"]);
        await TaskCreationGrant(f, s, s.Agent.PrincipalId, "Explicit Task source and own worker", ["agent-runs.create", "tasks.delegate", "tasks.read", "spaces.read", "sessions.read", "agents.read", "agents.chat"]);
        await f.AgentConfiguration().UpdateCooperationAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
            new(AcceptTasks: true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, initiator: s.Owner);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "tasks.delegate", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var approvals = ApprovalService(f, taskTargets: TaskCreations(f));
        var request = (await approvals.PrepareTaskDelegationAsync(new(s.Tenant, run.AgentRunId), JsonSerializer.Serialize(new {
            objective = "Exact assigned task", title = "Exact Task", target = new { instanceId = s.Agent.Instance.InstanceId }, requestKey = "exact-assignment"
        }), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(s.Tenant, request.Operation.TaskDelegation!.Task.TaskId));
        return (s, approvals, request, sessions);
    }
    [Theory] [InlineData(false, "secondary")] [InlineData(true, "secondary")]
    [InlineData(false, "reviewer")] [InlineData(true, "reviewer")]
    [InlineData(false, "configuration")] [InlineData(true, "configuration")]
    [InlineData(false, "forged")] [InlineData(true, "forged")]
    [InlineData(false, "replay")] [InlineData(true, "replay")]
    public async Task Tenant_task_creation_transaction_repeats_current_authority_and_exact_graph_before_publication(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PrepareTaskCreation(f); var s = v.Setup;
        var reviewer = boundary == "reviewer" ? await User(f, s, "task-reviewer@example.test",
            ["sessions.read", "tasks.delegate", "sessions.create", "agent-runs.create", "approvals.read", "approvals.approve", "agents.read", "spaces.read", "models.read"]) : s.Owner;
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, reviewer, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await v.Approvals.DispatchPendingAsync(); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var request = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, v.Request.RequestId))!;
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var current = await TaskCreations(f).RequireAsync(request, run, state, default);
        var admission = TenantTaskDelegationTarget.Build(request, current, state.Tenant.Revision, f.Ids.NewId());
        if (boundary == "secondary") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, v.Sessions.AssignmentId, state.Tenant.Revision);
        if (boundary == "reviewer")
        {
            var assignment = state.Assignments.Single(a => a.PrincipalId == reviewer.PrincipalId && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("sessions.create")));
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        if (boundary == "configuration")
        {
            var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(agent.Instance.InstanceId, agent.Instance.Revision, Persona: agent.Instance.Persona with { Name = "New current persona" }, ExpectedPersonaRevision: agent.Instance.PersonaRevision), f.Clock.GetUtcNow(),
                new(f.Ids.NewId(), s.Tenant, s.Owner.PrincipalId, "agents.update", agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", f.Clock.GetUtcNow())));
        }
        if (boundary == "forged") admission = admission with { WorkerSession = admission.WorkerSession! with { Title = "Substituted Task" } };
        state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var beforeRevision = state.Tenant.Revision; admission = admission with { ExpectedSecurityRevision = beforeRevision };
        var decision = (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, request.RequestId))!;
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, run.Claim!.Generation, run.Revision, request.Revision, decision.DecisionId, null,
            request.Operation.ActionHash, TenantOperationOutcome.Completed, "task_created", TaskDelegationAdmission: admission);
        async Task<TenantOperationReceipt?> Commit() => await ((ITenantApprovedOperationStore)f.Iam).TryCommitLocalEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantTaskConsentRules.AuditAction, request.ResourceId, "allowed", "task_created", f.Clock.GetUtcNow()));
        var receipt = await Commit(); Assert.NotNull(receipt);
        var expected = boundary == "replay" ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied;
        Assert.Equal(expected, receipt.Outcome);
        if (boundary == "replay")
        {
            Assert.Equal(receipt, await Commit()); Assert.Equal(admission.Task.TaskId, receipt.TaskAdmission!.TaskId);
            Assert.Equal(2, (await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(s.Tenant, null, 0, 100)).Items.Count);
        }
        else
        {
            Assert.Null(receipt.TaskAdmission); Assert.Single((await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(s.Tenant, null, 0, 100)).Items);
            Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, admission.Task.InitialWorkerRunId!.Value));
            Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(s.Tenant, admission.Task.TaskId));
        }
        Assert.Equal(beforeRevision + 1, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    }
    [Fact]
    public async Task Tenant_task_creation_SQLite_payload_is_immutable_and_populated_rollback_is_refused()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await PrepareTaskCreation(f); var request = v.Request;
        await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        foreach (var json in new[] { "{}", JsonSerializer.Serialize(new { taskId = Guid.NewGuid() }) })
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.TaskDelegation',json({json})) WHERE RequestId={request.RequestId}"))).SqliteErrorCode);
        Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261027040000_SessionGoals"))).SqliteErrorCode);
        Assert.Contains("20261027060000_TaskDelegation", await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(request.Operation.ActionHash, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(request.TenantId, request.RequestId))!.Operation.ActionHash);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_task_creation_inbox_filters_both_creation_permissions_before_totals(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PrepareTaskCreation(f); var s = v.Setup;
        var reader = await User(f, s, "creation-reader@example.test", ["sessions.read", "tasks.delegate", "agents.read", "spaces.read", "models.read", "sessions.create", "approvals.read"]);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
        await TaskCreationGrant(f, s, reader.PrincipalId, "Remaining creation read", ["agent-runs.create"]);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, page.Total); Assert.False(Assert.Single(page.Items).Review!.CanDecide);
        await TaskCreationGrant(f, s, reader.PrincipalId, "Task admission decision", ["approvals.approve"]);
        Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
    }
    [Fact]
    public async Task Tenant_task_creation_empty_SQLite_predecessor_upgrade_restores_current_guards()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        var options = new DbContextOptionsBuilder<AgentCore.Infrastructure.Persistence.AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(f.Root, "empty-migration.db")}").Options;
        await using var db = new AgentCore.Infrastructure.Persistence.AgentCoreDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261020100000_TenantBackgroundRuntime"); await db.Database.MigrateAsync();
        const string query = "SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_TenantApprovals_%' ORDER BY name";
        var before = await db.Database.SqlQueryRaw<string>(query).ToArrayAsync();
        Assert.Contains("TR_TenantApprovals_BackgroundCreation_Update", before);
        Assert.Contains("TR_TenantApprovals_IndependentTask_Insert", before); Assert.Contains("TR_TenantApprovals_IndependentTask_Update", before);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(query).ToArrayAsync());
    }

}
