using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, ArtifactRecord Item, RoleAssignment ReadAssignment,
        TenantArtifactRuntimeReadService Reads, TenantApprovalService Approvals, TenantApprovalRequest Request)> VerificationReviewFixture(Fixture f, bool checkpoint = false)
    {
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var item = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "scoped.md", "text/plain", new byte[] { 1 });
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Agent file reader", ["sessions.read", "artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var assignment = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "artifacts.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var reads = ArtifactReads(f); var approvals = ApprovalService(f, artifactReads: reads); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        if (checkpoint)
        {
            var call = new ModelToolCall("verify-scoped", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "verify_artifact", artifactId = item.ArtifactId }));
            run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        }
        var request = (await approvals.PrepareArtifactVerificationAsync(new(s.Tenant, frame.RunId), item.ArtifactId, run.Revision, frame.Generation)).Request!;
        return (s, frame, item, assignment, reads, approvals, request);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_verification_Review_filters_actual_file_and_Session_access_before_total_and_paging(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await VerificationReviewFixture(f); var s = v.Setup;
        var reader = await User(f, s, "metadata-reader@example.test", ["approvals.read"]);
        var fileRole = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read this exact file", ["artifacts.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var fileGrant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, fileRole.RoleId, TenantArtifactRules.Scope(s.Tenant, v.Item.ArtifactId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
        var sessionRole = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read this source Session", ["sessions.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var sessionGrant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, sessionRole.RoleId, TenantSessionRules.Scope(s.Tenant, v.Frame.SessionId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Single(page.Items); Assert.Equal(1, page.Total); Assert.False(page.Items[0].Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, fileGrant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, fileRole.RoleId, TenantArtifactRules.Scope(s.Tenant, v.Item.ArtifactId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, sessionGrant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_verification_rejected_root_cannot_bypass_consent_after_read_policy_relaxes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await VerificationReviewFixture(f, checkpoint: true); var s = v.Setup;
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await v.Approvals.DispatchPendingAsync(); var runs = (ITenantRunStore)f.Iam; var source = (await runs.FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "artifacts.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var rejected = await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, v.Request.RequestId);
        Assert.Equal(TenantApprovalRules.DuplicateReason(rejected!), (await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectAsync(
            new(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation), JsonSerializer.Serialize(new { artifactId = v.Item.ArtifactId }), 16000).AsTask())).ReasonCode);
        var repeated = await v.Approvals.PrepareArtifactVerificationAsync(new(s.Tenant, next.AgentRunId), v.Item.ArtifactId, next.Revision, next.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.Denied, repeated.Decision); Assert.Equal(v.Request.RequestId, repeated.Request!.RequestId);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }

    [Fact]
    public async Task Artifact_verification_SQLite_upgrade_reopen_and_shape_guards_preserve_exact_metadata_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261018153150_TenantArtifactReview");
            await db.Database.MigrateAsync();
        }
        var v = await VerificationReviewFixture(f); var s = v.Setup;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "session", "hash", "mixed", "grant" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(); var payload = JsonNode.Parse(row.PayloadJson)!;
            if (change == "session") payload["Operation"]!["ArtifactVerification"]!["SessionId"] = f.Ids.NewId().ToString("D");
            if (change == "hash") payload["Operation"]!["ArtifactVerification"]!["Sha256Hex"] = new string('z', 64);
            if (change == "mixed") payload["Operation"]!["ArtifactPublication"] = new JsonObject { ["SessionId"] = v.Frame.SessionId.ToString("D"), ["ArgumentsJson"] = "{}" };
            if (change == "grant") row.Kind = (int)ApprovalKind.ExceptionalGrant;
            row.PayloadJson = payload.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018153150_TenantArtifactReview"));
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }

    [Theory] [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "artifact")] [InlineData(true, "artifact")]
    public async Task Artifact_verification_missing_independent_raw_read_cannot_prepare_an_exceptional_grant(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Setup(f, "customer-support", 3); var frame = await ClaimArtifactRun(f, s);
        var item = await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(frame, f.Ids.NewId(), "protected.md", "text/plain", new byte[] { 1 });
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Incomplete file authority",
            missing == "session" ? ["artifacts.read"] : ["sessions.read", "artifacts.create"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var reads = ArtifactReads(f); var approvals = ApprovalService(f, artifactReads: reads);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => approvals.PrepareArtifactVerificationAsync(
            new(s.Tenant, run.AgentRunId), item.ArtifactId, run.Revision, frame.Generation).AsTask())).ReasonCode);
        Assert.Equal(0, (await approvals.ListAsync(s.Tenant, s.Owner, new())).Total);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "approvals.request");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_verification_reviewer_needs_independent_human_and_exact_underlying_approval_scope(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await VerificationReviewFixture(f); var s = v.Setup;
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "artifacts.read", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var ownView = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.True(ownView.Review!.IndependentReview); Assert.False(ownView.Review.CanDecide);
        var reviewer = await User(f, s, "file-metadata-reviewer@example.test", ["approvals.read", "sessions.read", "artifacts.read"]);
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reviewer)).Review!.CanDecide);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Approve this file read", ["approvals.approve"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reviewer.PrincipalId, role.RoleId, TenantArtifactRules.Scope(s.Tenant, v.Item.ArtifactId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reviewer); Assert.True(view.Review!.CanDecide);
        var decided = await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, reviewer, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(TenantApprovalStatus.Approved, decided.Status); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId));
    }
}
