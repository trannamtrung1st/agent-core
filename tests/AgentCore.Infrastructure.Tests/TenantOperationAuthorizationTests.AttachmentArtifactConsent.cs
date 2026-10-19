using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<SourceConsentCase> AttachmentConsent(Fixture f, bool grant = false)
    {
        var v = await AttachmentExportCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s, grant ? ["sessions.read"] : null);
        var reads = await GrantWorkspaceRuntimeReads(f, s, ["attachments.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "attachments.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var args = JsonSerializer.Serialize(new { path = "/attachments/" + v.Id.ToString("D"), displayName = "reviewed-original.txt" });
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), args, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Equal(v.Id, request.Operation.ArtifactPublication!.Source!.AttachmentId);
        return new(s, frame, approvals, request, args, v.Bytes, true, reads, s.Owner);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Attachment_export_Review_or_creation_grant_copies_only_saved_original_without_source_grants(bool sqlite, bool grant)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f, grant); var s = v.Setup;
        Assert.Equal(grant ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, v.Request.Kind);
        var run = await ResumeSourceConsent(f, v);
        var receipt = await Effects(f, artifactSources: ArtifactSources(f)).ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim!.Generation, v.Request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        Assert.Equal(v.Bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, run.SessionId, receipt.OperationId));
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "artifacts.read", TenantArtifactRules.Scope(s.Tenant, receipt.OperationId))).Allowed);
        if (grant) Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        else Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Attachment_export_inbox_filters_exact_raw_file_reads_before_counts_and_qualifies_scope(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f); var s = v.Setup;
        var reader = await User(f, s, "attachment-export-reviewer@example.test", ["sessions.read", "approvals.read", "artifacts.create"]);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact Attachment read", ["attachments.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var scope = TenantAttachmentRules.Scope(s.Tenant, v.Request.Operation.ArtifactPublication!.Source!.AttachmentId!.Value);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, page.Total); Assert.False(Assert.Single(page.Items).Review!.CanDecide);
        role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Session approval", ["approvals.approve"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, TenantSessionRules.Scope(s.Tenant, v.Frame.SessionId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Attachment_export_creation_grant_cannot_replace_revoked_raw_source_reads(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true, attachmentAuthority: true); var v = await AttachmentConsent(f, true); var s = v.Setup;
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, v.RawReads.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner)).Review!.CanDecide);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true, v.Request.Operation.ActionHash, (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, 1).AsTask());
        await v.Approvals.CancelAsync(s.Tenant, v.Request.RequestId, s.Owner, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId));
    }
}
