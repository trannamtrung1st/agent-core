using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_mutation_Review_rejects_changed_private_source_with_restored_length_and_timestamp(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await MutationReviewFixture(f, working, false); var s = v.Setup;
        var directory = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, v.Frame.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), s.Tenant, SpaceId(s));
        var path = Path.Combine(directory, "private.txt"); var stamp = File.GetLastWriteTimeUtc(path); var length = new FileInfo(path).Length;
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'X', (int)length).ToArray()); File.SetLastWriteTimeUtc(path, stamp);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        Assert.False(view.Review!.CanDecide); Assert.Equal("workspace_source_changed", view.Review.DecisionUnavailableReason);
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId,
            s.Owner, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision).AsTask())).ReasonCode);
        Assert.Equal(TenantApprovalStatus.Pending, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, v.Request.RequestId))!.Status);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_Review_refusal_survives_source_changes_and_policy_relaxation_at_service_and_atomic_direct_start(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await MutationReviewFixture(f, true, false); var s = v.Setup;
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        var next = source.Result?.ContinuationRunId is { } successor ? (await runs.FindTenantRunAsync(s.Tenant, successor))! : source;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.WhenRequesterLacksPermission, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await SeedRuntimeWorkspace(f, s, true, bytes: "Changed private source"u8.ToArray());
        var value = v.Request.Operation.WorkspaceMutation!; var call = new ModelToolCall("refused-write", value.ToolName, value.ArgumentsJson);
        var current = await WorkspaceEffectCheckpoint(f, frame, call);
        var result = await WorkspaceEffects(f).ExecuteAsync(frame, call);
        Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("previously_rejected", result.ReasonCode); Assert.Null(result.Receipt);
        var args = TenantWorkspaceMutationContract.Parse(frame.SessionId, call.Name, call.ArgumentsJson);
        var op = new AgentRunDirectOperation(f.Ids.NewId(), call.Id, call.Name, args.CanonicalArguments, args.ParametersHash, s.Agent.Instance.Revision, s.Agent.Instance.PersonaRevision);
        var commit = new TenantWorkspaceEffectCommit(current.AgentRunId, frame.Generation, current.Revision, op, TenantWorkspaceMutationContract.ActionHash(current, args.ParametersHash));
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var failure = await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantWorkspaceOperationStore)f.Iam).TryStartWorkspaceEffectAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantWorkspaceEffectRules.Dispatch, "working:" + frame.SessionId.ToString("D"), "allowed", "workspace_mutation_dispatched", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal("previously_rejected", failure.ReasonCode);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantWorkspaceEffectRules.Dispatch);
        var repeated = await v.Approvals.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), call, current.Revision, frame.Generation);
        Assert.Equal("previously_rejected", repeated.ReasonCode); Assert.Equal(v.Request.RequestId, repeated.Request!.RequestId);
    }

    [Theory] [InlineData(false, "workspaces.write")] [InlineData(true, "workspaces.write")]
    [InlineData(false, "workspaces.read")] [InlineData(true, "workspaces.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    public async Task Tenant_workspace_mutation_Review_requires_current_compound_independent_reviewer(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await SeedRuntimeWorkspace(f, s, false); await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        var peer = await User(f, s, "independent-mutation-reviewer@example.test", ["approvals.read", "approvals.approve", "agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var service = ApprovalService(f, workspaceMutations: MutationTargets(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await service.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new("independent-copy-review", ToolCatalog.WorkspaceCopy,
            """{"source":"/home/private.txt","destination":"/working/copy.txt"}"""), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Equal(ApprovalKind.Review, request.Kind);
        var ownerView = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        Assert.False(ownerView.Review!.CanDecide); Assert.True(ownerView.Review.IndependentReview);
        var peerView = await service.ReadViewAsync(s.Tenant, request.RequestId, peer);
        Assert.True(peerView.Review!.CanDecide, peerView.Review.DecisionUnavailableReason); Assert.True(peerView.Review.IndependentReview);
        Assert.Equal(TenantApprovalStatus.Approved, (await service.DecideAsync(s.Tenant, request.RequestId, peer, true, request.Operation.ActionHash, peerView.SecurityRevision, request.Revision)).Status);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_Review_does_not_inherit_missing_secondary_write_from_an_exact_delete_grant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await SeedRuntimeWorkspace(f, s, true); await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        var service = ApprovalService(f, workspaceMutations: MutationTargets(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var failure = await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new("delete-with-ungranted-write", ToolCatalog.WorkspaceBatch,
            """{"operations":[{"op":"delete","path":"/working/private.txt"},{"op":"mkdir","path":"/working/new"}]}"""), run.Revision, frame.Generation).AsTask());
        Assert.Equal("permission_denied", failure.ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 100))).Items);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_mutation_Review_mixed_batch_retains_independent_write_and_only_primary_delete_grant(bool sqlite, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await SeedRuntimeWorkspace(f, s, true);
        await GrantWorkspaceRuntimeReads(f, s, exceptional ? ["agents.read", "sessions.read", "workspaces.write"] : ["agents.read", "sessions.read", "workspaces.write", "workspaces.delete"]);
        var service = ApprovalService(f, workspaceMutations: MutationTargets(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await service.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new("mixed-batch-review", ToolCatalog.WorkspaceBatch,
            """{"operations":[{"op":"delete","path":"/working/private.txt"},{"op":"mkdir","path":"/working/new"}]}"""), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Equal("workspaces.delete", request.Permission); Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        Assert.Contains("workspaces.write", request.Operation.WorkspaceMutation!.TargetsJson);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason);
        await service.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId);
        Assert.Equal(exceptional, grant is not null); if (grant is not null) Assert.Equal(request.Operation.OperationId, grant.OperationId);
        var files = WorkspaceReader(f); var owner = WorkspaceOwner(s, true);
        Assert.NotEmpty((await files.InspectTenantAsync(owner, "/working/private.txt", 1024)).Bytes);
        Assert.DoesNotContain((await files.ListTenantAsync(owner, "/working", null, 100)).Items, item => item.LogicalPath == "/working/new");
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_Review_cross_root_copy_filters_independent_source_and_destination_scopes_before_counts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await SeedRuntimeWorkspace(f, s, false); await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var service = ApprovalService(f, workspaceMutations: MutationTargets(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await service.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new("copy-review", ToolCatalog.WorkspaceCopy,
            """{"source":"/home/private.txt","destination":"/working/copy.txt"}"""), run.Revision, frame.Generation)).Request!;
        var reader = await User(f, s, "scoped-mutation-reviewer@example.test", ["approvals.read", "sessions.read"]);
        async Task<RoleAssignment> Grant(string key, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, key + " reviewer", [key], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        await Grant("workspaces.write", TenantWorkspaceRules.Working(s.Tenant, frame.SessionId));
        var page = await service.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        await Grant("workspaces.read", TenantWorkspaceRules.Home(s.Tenant, SpaceId(s)));
        page = await service.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        var parent = await Grant("agents.read", AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")));
        page = await service.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, page.Total); Assert.Equal(request.RequestId, Assert.Single(page.Items).Request.RequestId);
        Assert.False(page.Items[0].Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, parent.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await service.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadViewAsync(s.Tenant, request.RequestId, reader).AsTask());
    }

    [Fact]
    public async Task Tenant_workspace_mutation_Review_SQLite_upgrade_reopen_shapes_and_downgrade_preserve_exact_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext()) { await db.GetService<IMigrator>().MigrateAsync("20261019013030_TenantWorkspaceDirectEffects"); await db.Database.MigrateAsync(); }
        var v = await MutationReviewFixture(f, false, false); var s = v.Setup;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "session", "hash", "mixed", "permission", "owner", "missing-working", "empty-targets", "array-args" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(); var payload = JsonNode.Parse(row.PayloadJson)!;
            var mutation = payload["Operation"]!["WorkspaceMutation"]!;
            if (change == "session") mutation["SessionId"] = f.Ids.NewId().ToString("D");
            if (change == "hash") mutation["ObservationHash"] = new string('z', 64);
            if (change == "mixed") payload["Operation"]!["ArtifactPublication"] = new JsonObject { ["SessionId"] = v.Frame.SessionId.ToString("D"), ["ArgumentsJson"] = "{}" };
            if (change == "permission") row.Permission = "workspaces.read";
            if (change == "owner") row.ResourceId = "working:" + v.Frame.SessionId.ToString("D");
            if (change == "missing-working") mutation.AsObject().Remove("Working");
            if (change == "empty-targets") mutation["TargetsJson"] = "[]";
            if (change == "array-args") mutation["ArgumentsJson"] = "[]";
            row.PayloadJson = payload.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019013030_TenantWorkspaceDirectEffects"));
        await using (var restored = f.Contexts!.CreateDbContext()) await restored.Database.MigrateAsync();
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, v.Request.RequestId));
    }
}
