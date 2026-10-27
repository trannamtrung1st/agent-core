using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, TenantWorkspaceRuntimeReadService Reads,
        TenantApprovalService Approvals, TenantApprovalRequest Request, TenantSessionInitiator? Peer)> WorkspaceReviewFixture(Fixture f, bool working = false, bool list = false, string permission = "workspaces.read", bool independent = false, bool search = false)
    {
        var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, working); await GrantWorkspaceRuntimeReads(f, s);
        if (search) await SeedRuntimeWorkspace(f, s, working, "nonmatch.txt", System.Text.Encoding.UTF8.GetBytes("No matching information."));
        var peer = independent ? await User(f, s, "independent-workspace-reviewer@example.test", ["approvals.read", "approvals.approve", "agents.read", "sessions.read", "workspaces.read"]) : null;
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, independent), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var path = working ? "/working" : "/home"; if (!list && !search) path += "/private.txt";
        var tool = search ? ToolCatalog.WorkspaceSearch : list ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead; var reads = WorkspaceRuntimeReads(f); var approvals = ApprovalService(f, workspaceReads: reads);
        var arguments = search ? JsonSerializer.Serialize(new { path, query = "Private", glob = "**/*.txt", maxResults = 10 }) : JsonSerializer.Serialize(new { path });
        var call = new ModelToolCall("saved-workspace-read", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } }));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var intent = TenantApprovalToolContract.Parse(call).WorkspaceRead!;
        var request = (await approvals.PrepareWorkspaceReadAsync(new(s.Tenant, frame.RunId), new(call.Id, intent.Tool, intent.ArgumentsJson), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request);
        return (s, frame, reads, approvals, request, peer);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Workspace_read_Review_rejects_changed_bytes_even_when_file_size_and_timestamp_are_restored(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working); var s = v.Setup;
        var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, s.Run.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), s.Tenant, SpaceId(s));
        var file = Path.Combine(physical, "private.txt"); var stamp = File.GetLastWriteTimeUtc(file); var length = new FileInfo(file).Length;
        await File.WriteAllBytesAsync(file, Enumerable.Repeat((byte)'X', (int)length).ToArray()); File.SetLastWriteTimeUtc(file, stamp);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.False(view.Review!.CanDecide); Assert.Equal("workspace_source_changed", view.Review.DecisionUnavailableReason);
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner,
            true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision).AsTask())).ReasonCode);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workspace_read_Review_rejects_a_changed_saved_directory_page(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working: true, list: true);
        await SeedRuntimeWorkspace(f, v.Setup, true, "additional.txt");
        var view = await v.Approvals.ReadViewAsync(v.Setup.Tenant, v.Request.RequestId, v.Setup.Owner);
        Assert.False(view.Review!.CanDecide); Assert.Equal("workspace_source_changed", view.Review.DecisionUnavailableReason);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Workspace_read_Review_filters_workspace_and_parent_reads_before_counts_and_paging(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working); var s = v.Setup;
        var reader = await User(f, s, "scoped-workspace-reviewer@example.test", ["approvals.read", "sessions.read"]);
        var scope = TenantWorkspaceRules.Scope(WorkspaceOwner(s, working));
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Inspect exact saved workspace", ["workspaces.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(working ? 1 : 0, page.Total);
        if (!working)
        {
            var parent = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Inspect owning Instance", ["agents.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, parent.RoleId, AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Single(page.Items); Assert.Equal(1, page.Total); Assert.False(page.Items[0].Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Workspace_read_Review_rejection_cannot_be_bypassed_by_policy_relaxation_or_source_changes_in_the_same_root(bool sqlite) => WorkspaceReadRefusal(sqlite);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_workspace_search_refusal_cannot_be_bypassed_by_policy_or_source_changes(bool sqlite) => WorkspaceReadRefusal(sqlite, search: true);
    private async Task WorkspaceReadRefusal(bool sqlite, bool search = false)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, search: search); var s = v.Setup;
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await SeedRuntimeWorkspace(f, s, false, bytes: "Changed source does not authorize a refused read"u8.ToArray());
        var call = new ModelToolCall("same-refused-read", v.Request.Operation.WorkspaceRead!.ToolName, v.Request.Operation.WorkspaceRead.ArgumentsJson);
        Assert.Equal("previously_rejected", (await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectAsync(new(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation), call, 16000).AsTask())).ReasonCode);
        var again = await v.Approvals.PrepareWorkspaceReadAsync(new(s.Tenant, next.AgentRunId), call, next.Revision, next.Claim!.Generation);
        Assert.Equal("previously_rejected", again.ReasonCode); Assert.Equal(v.Request.RequestId, again.Request!.RequestId);
    }
}
