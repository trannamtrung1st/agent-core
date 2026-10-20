using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantBackgroundCreationTarget BackgroundCreations(Fixture f) => new((ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.RunConfiguration(), f.Authorizer);
    private static async Task<RoleAssignment> BackgroundCreationGrant(Fixture f, Environment s, Guid principal, string name, string[] permissions)
    {
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, name, permissions, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, principal, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    }
    private static async Task<(Environment Setup, TenantApprovalService Approvals, TenantApprovalRequest Request, RoleAssignment Sessions)> PrepareBackgroundCreation(Fixture f)
    {
        var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        var sessions = await BackgroundCreationGrant(f, s, s.Agent.PrincipalId, "Independent Session creation", ["sessions.create"]);
        await BackgroundCreationGrant(f, s, s.Agent.PrincipalId, "Independent Run creation", ["agent-runs.create"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "sessions.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var approvals = ApprovalService(f, backgroundCreations: BackgroundCreations(f));
        var request = (await approvals.PrepareBackgroundStartAsync(new(s.Tenant, run.AgentRunId), """{"objective":"Exact child task","title":"Exact child","reportCompletion":true}""", run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); return (s, approvals, request, sessions);
    }
    [Theory] [InlineData(false, "secondary")] [InlineData(true, "secondary")]
    [InlineData(false, "reviewer")] [InlineData(true, "reviewer")]
    [InlineData(false, "configuration")] [InlineData(true, "configuration")]
    [InlineData(false, "forged")] [InlineData(true, "forged")]
    [InlineData(false, "replay")] [InlineData(true, "replay")]
    public async Task Tenant_background_creation_transaction_repeats_current_authority_and_exact_graph_before_any_publication(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PrepareBackgroundCreation(f); var s = v.Setup;
        var reviewer = boundary == "reviewer" ? await User(f, s, "background-reviewer@example.test", ["sessions.read", "sessions.create", "agent-runs.create", "approvals.read", "approvals.approve"]) : s.Owner;
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, reviewer, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await v.Approvals.DispatchPendingAsync(); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var request = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, v.Request.RequestId))!;
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var current = await BackgroundCreations(f).RequireAsync(request, run, state, default);
        var admission = TenantBackgroundCreationTarget.Build(request, run, current.Session, current.Configuration, f.Clock.GetUtcNow());
        if (boundary == "secondary") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, v.Sessions.AssignmentId, state.Tenant.Revision);
        if (boundary == "reviewer")
        {
            var assignment = state.Assignments.Single(a => a.PrincipalId == reviewer.PrincipalId && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("sessions.create")));
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        if (boundary == "configuration")
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(s.Agent.Instance.InstanceId, 1, Persona: s.Agent.Instance.Persona with { Name = "New current persona" }, ExpectedPersonaRevision: 1), f.Clock.GetUtcNow(),
                new(f.Ids.NewId(), s.Tenant, s.Owner.PrincipalId, "agents.update", s.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", f.Clock.GetUtcNow())));
        if (boundary == "forged") admission = admission with { Session = admission.Session with { Title = "Substituted task" } };
        state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var beforeRevision = state.Tenant.Revision;
        var decision = (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, request.RequestId))!;
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, run.Claim!.Generation, run.Revision, request.Revision, decision.DecisionId, null,
            request.Operation.ActionHash, TenantOperationOutcome.Completed, "background_created", BackgroundAdmission: admission);
        async Task<TenantOperationReceipt?> Commit() => await ((ITenantApprovedOperationStore)f.Iam).TryCommitLocalEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantBackgroundCreationRules.AuditAction, request.ResourceId, "allowed", "background_created", f.Clock.GetUtcNow()));
        if (boundary == "replay")
        {
            var receipt = await Commit(); Assert.Equal(TenantOperationOutcome.Completed, receipt!.Outcome);
            Assert.Equal(receipt, await Commit());
            Assert.Equal(beforeRevision + 1, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            Assert.Equal(2, (await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(s.Tenant, null, 0, 100)).Items.Count);
            Assert.Equal(run.Revision + 1, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Revision);
            Assert.NotNull(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, request.Operation.BackgroundStart!.RunId));
            return;
        }
        if (boundary == "configuration") Assert.Equal(TenantOperationOutcome.Denied, (await Commit())!.Outcome);
        else
        {
            if (boundary == "forged") Assert.Equal("operation_mismatch", (await Assert.ThrowsAsync<SecurityConflictException>(async () => await Commit())).ReasonCode);
            else Assert.Equal(boundary == "secondary" ? "operation_authority_changed" : "approval_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(async () => await Commit())).ReasonCode);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, commit.OperationId));
            Assert.Equal(beforeRevision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Revision);
        }
        Assert.Single((await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(s.Tenant, null, 0, 100)).Items);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, request.Operation.BackgroundStart!.RunId));
    }
    [Fact]
    public async Task Tenant_background_creation_SQLite_shape_retains_old_guards_and_refuses_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var v = await PrepareBackgroundCreation(f); var request = v.Request;
        await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            await db.Database.OpenConnectionAsync(); command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name IN ('TR_TenantApprovals_BrowserArtifacts_Insert','TR_TenantApprovals_BrowserArtifacts_Update','TR_TenantReceipts_BrowserArtifacts_Insert','TR_TenantReceipts_BrowserArtifacts_Update','TR_TenantApprovals_BrowserFiles_Insert','TR_TenantApprovals_BrowserFiles_Update','TR_TenantApprovals_BackgroundCreation_Update')";
            Assert.Equal(7L, await command.ExecuteScalarAsync());
        }
        foreach (var json in new[] { "{}", JsonSerializer.Serialize(request.Operation.BackgroundStart! with { ChildSessionId = request.SessionId }),
            JsonSerializer.Serialize(request.Operation.BackgroundStart! with { Objective = "Changed after preparation" }) })
            Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.BackgroundStart',json({json})) WHERE RequestId={request.RequestId}"))).SqliteErrorCode);
        Assert.Equal(request.Operation.ActionHash, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(request.TenantId, request.RequestId))!.Operation.ActionHash);
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020100000_TenantBackgroundRuntime"));
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(request.Operation.OperationId, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(request.TenantId, request.RequestId))!.Operation.OperationId);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_creation_inbox_filters_both_raw_creation_permissions_before_totals_and_keeps_decision_separate(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PrepareBackgroundCreation(f); var s = v.Setup;
        var reader = await User(f, s, "creation-reader@example.test", ["sessions.read", "sessions.create", "approvals.read"]);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
        await BackgroundCreationGrant(f, s, reader.PrincipalId, "Independent remaining creation read", ["agent-runs.create"]);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, page.Total); Assert.False(Assert.Single(page.Items).Review!.CanDecide);
        await BackgroundCreationGrant(f, s, reader.PrincipalId, "Tenant creation decision", ["approvals.approve"]);
        Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
    }
    [Fact]
    public async Task Tenant_background_creation_empty_SQLite_downgrade_upgrade_restores_current_guards()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261020100000_TenantBackgroundRuntime"); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        await using var command = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_TenantApprovals_%'";
        Assert.Equal(5L, await command.ExecuteScalarAsync());
    }

}
