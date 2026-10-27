using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Tenant_workspace_search_SQLite_upgrade_reopen_and_guarded_rollback_retain_exact_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext())
        { await db.GetService<IMigrator>().MigrateAsync("20261018221227_TenantWorkspaceReadReview"); await db.Database.MigrateAsync(); }
        var v = await WorkspaceReviewFixture(f, search: true);
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
        await using (var db = f.Contexts!.CreateDbContext())
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018221227_TenantWorkspaceReadReview"));
        await using (var restored = f.Contexts!.CreateDbContext()) await restored.Database.MigrateAsync();
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_search_empty_scope_does_not_initialize_and_file_and_depth_limits_remain_bounded(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s); var reads = WorkspaceRuntimeReads(f);
        var physical = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, s.Run.SessionId);
        var call = new ModelToolCall("bounded-search", ToolCatalog.WorkspaceSearch, """{"path":"/working","query":"needle"}""");
        using (var empty = JsonDocument.Parse(await reads.InspectAsync(frame, call, 16000))) Assert.Empty(empty.RootElement.GetProperty("matches").EnumerateArray());
        Assert.False(Directory.Exists(physical));
        var fs = new CanonicalWorkspaceFilesystem(physical, "/working"); fs.Ensure();
        var large = Encoding.UTF8.GetBytes("needle".PadRight(WorkspaceSearchLimits.MaxFileBytesToScan + 1, 'X'));
        await fs.WriteAsync("/working/large.dat", large, large.Length, 2_000_000);
        using (var skipped = JsonDocument.Parse(await reads.InspectAsync(frame, call, 16000))) Assert.Empty(skipped.RootElement.GetProperty("matches").EnumerateArray());
        for (var i = 0; i < WorkspaceSearchLimits.MaxFilesScanned; i++) await fs.WriteAsync($"/working/capped/a{i:D3}.txt", Array.Empty<byte>(), 4096, 2_000_000);
        await fs.WriteAsync("/working/capped/z-needle.txt", "needle outside bounded prefix"u8.ToArray(), 4096, 2_000_000);
        using (var capped = JsonDocument.Parse(await reads.InspectAsync(frame, call with { ArgumentsJson = """{"path":"/working/capped","query":"needle"}""" }, 16000)))
        { Assert.Empty(capped.RootElement.GetProperty("matches").EnumerateArray()); Assert.True(capped.RootElement.GetProperty("truncated").GetBoolean()); }
        using (var exact = JsonDocument.Parse(await reads.InspectAsync(frame, call with { ArgumentsJson = """{"path":"/working/capped/z-needle.txt","query":"needle"}""" }, 16000)))
            Assert.Single(exact.RootElement.GetProperty("matches").EnumerateArray());
        var deep = "/working/deep/" + string.Join('/', Enumerable.Range(0, WorkspaceSearchLimits.MaxDepth + 1).Select(i => "d" + i)) + "/needle.txt";
        await fs.WriteAsync(deep, "needle outside depth bound"u8.ToArray(), 4096, 2_000_000);
        using var depth = JsonDocument.Parse(await reads.InspectAsync(frame, call with { ArgumentsJson = """{"path":"/working/deep","query":"needle"}""" }, 16000));
        Assert.Empty(depth.RootElement.GetProperty("matches").EnumerateArray()); Assert.True(depth.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_search_withholds_matches_when_executor_read_is_revoked_during_traversal(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, working); var grant = await GrantWorkspaceRuntimeReads(f, s);
        var probe = new WorkspaceRuntimeReadProbe(WorkspaceReader(f), async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant,
            grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var call = new ModelToolCall("raced-search", ToolCatalog.WorkspaceSearch, JsonSerializer.Serialize(new { path = working ? "/working" : "/home", query = "Private" }));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceRuntimeReads(f, probe).InspectAsync(frame, call, 16000).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Fired);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_search_Review_detects_changed_negative_content_with_same_length_and_restored_timestamp(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working: working, search: true); var s = v.Setup;
        var root = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, s.Run.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), s.Tenant, SpaceId(s));
        var path = Path.Combine(root, "nonmatch.txt"); var stamp = File.GetLastWriteTimeUtc(path); var length = new FileInfo(path).Length;
        await File.WriteAllTextAsync(path, "Private".PadRight((int)length, 'X')); File.SetLastWriteTimeUtc(path, stamp);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        Assert.False(view.Review!.CanDecide); Assert.Equal("workspace_source_changed", view.Review.DecisionUnavailableReason);
        await Assert.ThrowsAsync<SecurityConflictException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true,
            v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision).AsTask());
        Assert.DoesNotContain("Private model bytes", JsonSerializer.Serialize(view.Request));
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_search_Review_revalidates_source_after_decision_before_successor_delivery(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceReviewFixture(f, working: true, search: true); var s = v.Setup;
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        await SeedRuntimeWorkspace(f, s, true, "new.txt", "Private newly discovered body"u8.ToArray());
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var result = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, workspaceReads: v.Reads), f.Clock, runtime, f.Ids).ResumeAsync(next);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome!.Outcome);
        Assert.DoesNotContain("Private model bytes", result.Run.Checkpoint!.PayloadJson); Assert.DoesNotContain("newly discovered", result.Run.Checkpoint.PayloadJson);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_search_returns_actual_bounded_name_and_UTF8_content_matches_under_current_authority(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); var root = working ? "/working" : "/home";
        await SeedRuntimeWorkspace(f, s, working, "report-name.txt", "Unrelated literal body"u8.ToArray());
        await SeedRuntimeWorkspace(f, s, working, "nested/note.txt", Encoding.UTF8.GetBytes("Unicode report: thử nghiệm\nExact content"));
        await SeedRuntimeWorkspace(f, s, working, "skip.dat", "report in excluded extension"u8.ToArray());
        await SeedRuntimeWorkspace(f, s, working, "binary.txt", [0, 255, 114, 101, 112, 111, 114, 116]);
        var reads = WorkspaceRuntimeReads(f); var call = new ModelToolCall("search", ToolCatalog.WorkspaceSearch,
            JsonSerializer.Serialize(new { path = root, query = "REPORT", glob = "**/*.txt", maxResults = 10 }));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(frame, call, 16000).AsTask())).ReasonCode);
        var grant = await GrantWorkspaceRuntimeReads(f, s);
        var output = await reads.InspectAsync(frame, call, 16000);
        using var doc = JsonDocument.Parse(output); var result = doc.RootElement;
        Assert.True(result.GetProperty("tenantWorkspaceRead").GetBoolean()); Assert.True(result.GetProperty("untrusted").GetBoolean()); Assert.False(result.GetProperty("truncated").GetBoolean());
        var matches = result.GetProperty("matches").EnumerateArray().ToArray(); Assert.Equal(2, matches.Length);
        Assert.Contains(matches, m => m.GetProperty("path").GetString() == root + "/report-name.txt" && m.GetProperty("kind").GetString() == "name");
        Assert.Contains(matches, m => m.GetProperty("path").GetString() == root + "/nested/note.txt" && m.GetProperty("snippet").GetString()!.Contains("thử nghiệm"));
        Assert.DoesNotContain(f.Root, output); Assert.DoesNotContain("excluded extension", output);
        using var limited = JsonDocument.Parse(await reads.InspectAsync(frame, call with { ArgumentsJson = JsonSerializer.Serialize(new { path = root, query = "report", maxResults = 1 }) }, 16000));
        Assert.Single(limited.RootElement.GetProperty("matches").EnumerateArray()); Assert.True(limited.RootElement.GetProperty("truncated").GetBoolean());
        using var tiny = JsonDocument.Parse(await reads.InspectAsync(frame, call, 80)); Assert.Equal("output_limit", tiny.RootElement.GetProperty("error").GetString());
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, output, ToolCallId: call.Id, Name: call.Name)]), 1, output.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredAsync(run);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask());
    }
}
