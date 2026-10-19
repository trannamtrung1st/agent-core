using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private sealed record AttachmentConsentCase(Environment Setup, AgentRun Source, Guid Id, RoleAssignment Grant, TenantApprovalService Approvals,
        TenantAttachmentRuntimeContextService Reads, ContextFileProbe Probe, TenantApprovalRequest Request);
    private static async Task<AttachmentConsentCase> AttachmentConsent(Fixture f)
    {
        var v = await ContextCase(f); var s = v.Setup;
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Owner, "attachments.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var call = new ModelToolCall("exact-file-review", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "read_attachment", read = new { attachmentId = v.Id } }));
        var source = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(v.Run.Owner, v.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(v.Run.Revision, f.Clock.GetUtcNow(), v.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])], executionState: new(TranscriptId: v.Run.ResponseId)), 1, 0, 60000, 100), null), f.Ids.NewId());
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments); var reads = ContextService(f, probe); var approvals = AttachmentApprovals(f, reads);
        var request = (await approvals.PrepareAttachmentReadAsync(new(s.Tenant, source.AgentRunId), JsonSerializer.Serialize(new { attachmentId = v.Id }), source.Revision, source.Claim!.Generation)).Request!;
        return new(s, source, v.Id, v.Grant, approvals, reads, probe, request);
    }
    private static async Task<TenantSessionInitiator> AttachmentReviewer(Fixture f, Environment s, string[] permissions)
    {
        const string login = "attachment-reviewer@example.test", password = "strong-reviewer-password";
        var account = await f.Identity.RegisterAsync(login, "Reviewer", password);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Attachment reviewer", permissions, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(s.Owner.PrincipalId, s.Tenant, login, role.RoleId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        return new(account.AccountId, (await f.Identity.SignInAsync(login, password))!.Session.SessionId, user.PrincipalId);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    public async Task Tenant_attachment_Review_cannot_replace_missing_raw_Agent_read(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true);
        var v = await ContextCase(f, new[] { "sessions.read", "attachments.read" }.Where(p => p != missing).ToArray());
        var probe = new ContextFileProbe((ITenantAttachmentStore)f.Attachments); var service = AttachmentApprovals(f, ContextService(f, probe));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareAttachmentReadAsync(new(v.Setup.Tenant, v.Run.AgentRunId), JsonSerializer.Serialize(new { attachmentId = v.Id }), v.Run.Revision, v.Run.Claim!.Generation).AsTask());
        Assert.Equal(0, probe.Reads); Assert.Equal(0, (await service.ListAsync(v.Setup.Tenant, v.Setup.Owner, new())).Total);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(v.Setup.Tenant, 200), a => a.Action == "approvals.request");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_Review_scopes_inventory_before_counts_and_requires_independent_qualified_reviewer(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f); var s = v.Setup;
        var reviewer = await AttachmentReviewer(f, s, ["approvals.read", "sessions.read"]);
        var empty = await v.Approvals.ListAsync(s.Tenant, reviewer, new(0, 1)); Assert.Empty(empty.Items); Assert.Equal(0, empty.Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reviewer).AsTask());
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact source read", ["attachments.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reviewer.PrincipalId, role.RoleId, TenantAttachmentRules.Scope(s.Tenant, v.Id), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var visible = await v.Approvals.ListAsync(s.Tenant, reviewer, new(0, 1)); Assert.Single(visible.Items); Assert.Equal(1, visible.Total);
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reviewer)).Review!.CanDecide);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Owner, "attachments.read", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var own = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.True(own.Review!.IndependentReview); Assert.False(own.Review.CanDecide);
        role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact source approval", ["approvals.approve"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reviewer.PrincipalId, role.RoleId, TenantAttachmentRules.Scope(s.Tenant, v.Id), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reviewer); Assert.True(view.Review!.CanDecide);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, reviewer, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(0, v.Probe.Reads); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_Review_stale_Agent_read_blocks_decision_but_preserves_cancellation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f); var s = v.Setup;
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision).AsTask());
        await v.Approvals.CancelAsync(s.Tenant, v.Request.RequestId, s.Owner, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(0, v.Probe.Reads); Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_attachment_Review_root_refusal_survives_relaxed_policy_for_direct_and_repeat_requests(bool sqlite, bool cancel)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f); var s = v.Setup;
        var rev = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        if (cancel) await v.Approvals.CancelAsync(s.Tenant, v.Request.RequestId, s.Owner, v.Request.Operation.ActionHash, rev, v.Request.Revision);
        else await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, rev, v.Request.Revision);
        await v.Approvals.DispatchPendingAsync(); var runs = (ITenantRunStore)f.Iam; var source = (await runs.FindTenantRunAsync(s.Tenant, v.Source.AgentRunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var resumed = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, AttachmentEffects(f, v.Reads), f.Clock, (ITenantRuntimeStore)f.Iam, f.Ids, attachmentReads: v.Reads).ResumeAsync(next);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(s.Tenant, s.Owner, "attachments.read", new(OperationApprovalMode.WhenRequesterLacksPermission, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var args = v.Request.Operation.AttachmentRead!.ArgumentsJson; next = resumed.Run;
        var refused = await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectReadAsync(new(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation), args, false, ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal(cancel ? "previously_cancelled" : "previously_rejected", refused.ReasonCode);
        var again = await v.Approvals.PrepareAttachmentReadAsync(new(s.Tenant, next.AgentRunId), args, next.Revision, next.Claim!.Generation);
        Assert.Equal(v.Request.RequestId, again.Request!.RequestId); Assert.Equal(OperationDecisionKind.Denied, again.Decision); Assert.Equal(1, (await v.Approvals.ListAsync(s.Tenant, s.Owner, new())).Total); Assert.Equal(0, v.Probe.Reads);
    }
    [Fact]
    public async Task Tenant_attachment_Review_SQLite_shape_rejects_mixed_source_grants_and_unsafe_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f);
        foreach (var mutation in new[] { "source", "mixed", "grant" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync();
            var payload = System.Text.Json.Nodes.JsonNode.Parse(row.PayloadJson)!;
            if (mutation == "source") payload["Operation"]!["AttachmentRead"]!["SessionId"] = Guid.NewGuid().ToString("D");
            if (mutation == "mixed") payload["Operation"]!["ArtifactVerification"] = new System.Text.Json.Nodes.JsonObject();
            if (mutation == "grant") row.Kind = (int)ApprovalKind.ExceptionalGrant;
            row.PayloadJson = payload.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019092834_TenantAttachmentOwnership"));
        Assert.Equal(v.Request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
    }
}
