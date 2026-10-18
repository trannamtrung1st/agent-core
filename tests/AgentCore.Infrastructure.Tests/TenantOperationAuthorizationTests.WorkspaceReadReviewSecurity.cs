using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "workspaces.read")] [InlineData(true, "workspaces.read")]
    public async Task Workspace_read_Review_enforces_current_compound_independent_reviewer_policy(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, permission: permission, independent: true); var s = v.Setup;
        var original = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.False(original.Review!.CanDecide); Assert.True(original.Review.IndependentReview);
        var peer = v.Peer!;
        var allowed = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, peer); Assert.True(allowed.Review!.CanDecide); Assert.True(allowed.Review.IndependentReview);
        var approved = await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, peer, true, v.Request.Operation.ActionHash, allowed.SecurityRevision, v.Request.Revision);
        Assert.Equal(TenantApprovalStatus.Approved, approved.Status); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, approved.RequestId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workspace_read_Review_does_not_deliver_after_the_deciding_human_loses_exact_workspace_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working: true); var s = v.Setup;
        var peer = await User(f, s, "revoked-workspace-reviewer@example.test", ["approvals.read", "approvals.approve", "sessions.read"]);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read reviewed working files", ["workspaces.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, peer.PrincipalId, role.RoleId, TenantWorkspaceRules.Working(s.Tenant, s.Run.SessionId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, peer); Assert.True(view.Review!.CanDecide);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, peer, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, workspaceReads: v.Reads), f.Clock, runtime, f.Ids, workspaceReads: v.Reads);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Denied, result.Outcome!.Outcome);
        Assert.DoesNotContain("Private model bytes", result.Run.Checkpoint!.PayloadJson);
        var receipt = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId);
        Assert.Equal(TenantOperationOutcome.Denied, receipt!.Outcome);
    }
    [Fact]
    public async Task Workspace_read_Review_SQLite_upgrade_reopen_shape_and_downgrade_guards_preserve_saved_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext())
        { await db.GetService<IMigrator>().MigrateAsync("20261018183653_TenantArtifactVerification"); await db.Database.MigrateAsync(); }
        var v = await WorkspaceReviewFixture(f); var s = v.Setup;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "session", "hash", "mixed", "grant", "owner", "missing-working" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(); var payload = JsonNode.Parse(row.PayloadJson)!;
            if (change == "session") payload["Operation"]!["WorkspaceRead"]!["SessionId"] = f.Ids.NewId().ToString("D");
            if (change == "hash") payload["Operation"]!["WorkspaceRead"]!["ObservationHash"] = new string('z', 64);
            if (change == "mixed") payload["Operation"]!["ArtifactPublication"] = new JsonObject { ["SessionId"] = v.Frame.SessionId.ToString("D"), ["ArgumentsJson"] = "{}" };
            if (change == "grant") row.Kind = (int)ApprovalKind.ExceptionalGrant;
            if (change == "owner") row.ResourceId = "working:" + v.Frame.SessionId.ToString("D");
            if (change == "missing-working") payload["Operation"]!["WorkspaceRead"]!.AsObject().Remove("Working");
            row.PayloadJson = payload.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018183653_TenantArtifactVerification"));
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }
}
