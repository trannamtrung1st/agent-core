using System.Text;
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
    private static TenantWorkspaceRuntimeReadService WorkspaceRuntimeReads(Fixture f, ITenantWorkspaceReadStore? store = null, IOfficeProcessor? office = null, TenantResourceSourceTarget? resourceSources = null) => new(Operations(f),
        (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam, store ?? WorkspaceReader(f), f.RunConfiguration(), f.Iam, f.Authorizer, f.Clock, (ITenantApprovalStore)f.Iam, f.Ids, (ITenantApprovedOperationStore)f.Iam, office, resourceSources ?? ResourceSources(f));
    private static async Task<RoleAssignment> GrantWorkspaceRuntimeReads(Fixture f, Environment s, string[]? keys = null, AccessScope? scope = null)
    {
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Owned model workspace reads", keys ?? ["agents.read", "sessions.read", "workspaces.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, scope ?? AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    }
    private static async Task SeedRuntimeWorkspace(Fixture f, Environment s, bool working, string name = "private.txt", byte[]? bytes = null, long maxFileBytes = 4096)
    {
        var root = Path.Combine(f.Root, "workspaces"); _ = WorkspaceReader(f);
        var fs = new CanonicalWorkspaceFilesystem(working ? AgentWorkspacePhysicalPaths.WorkingDirectory(root, s.Agent.Instance.InstanceId, s.Run.SessionId)
            : AgentWorkspacePhysicalPaths.HomeDirectory(root, s.Agent.Instance.InstanceId), working ? "/working" : "/home");
        fs.Ensure(); await fs.WriteAsync((working ? "/working/" : "/home/") + name, bytes ?? Encoding.UTF8.GetBytes("Private model bytes: thử nghiệm"), maxFileBytes, 100000);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_model_reads_and_lists_use_actual_files_and_executor_authority(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, working);
        var service = WorkspaceRuntimeReads(f); var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock, workspaceReads: service));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId,
            SupportsTools: true, AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation);
        var root = working ? "/working" : "/home"; var call = new ModelToolCall("actual-workspace-read", ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = root + "/private.txt" }));
        async Task<JsonDocument> Execute(ModelToolCall? c = null, ToolExecutionAdmission? a = null) => JsonDocument.Parse((await executor.ExecuteAsync(s.Run.Admission.Configuration!.Definition, frame.SessionId, c ?? call, 16000, admission: a ?? admission)).Text);
        using (var denied = await Execute()) Assert.Equal("executor_read_required", denied.RootElement.GetProperty("reasonCode").GetString());
        var grant = await GrantWorkspaceRuntimeReads(f, s);
        using (var read = await Execute())
        {
            Assert.Equal("Private model bytes: thử nghiệm", read.RootElement.GetProperty("content").GetString()); Assert.True(read.RootElement.GetProperty("tenantWorkspaceRead").GetBoolean());
            Assert.DoesNotContain(f.Root, read.RootElement.GetRawText()); Assert.False(read.RootElement.TryGetProperty("writable", out _));
        }
        using (var list = await Execute(new("actual-workspace-list", ToolCatalog.WorkspaceList, JsonSerializer.Serialize(new { path = root, limit = 1 }))))
            Assert.Equal(root + "/private.txt", Assert.Single(list.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        using (var missing = await Execute(a: admission with { AgentRunGeneration = f.Ids.NewId() })) Assert.True(missing.RootElement.TryGetProperty("error", out _));
        using (var foreign = await Execute(a: admission with { TenantId = f.Ids.NewId() })) Assert.True(foreign.RootElement.TryGetProperty("error", out _));
        using (var invalid = await Execute(call with { ArgumentsJson = "{\"path\":\"" + root + "/private.txt\",\"tenantId\":\"" + s.Tenant + "\"}" })) Assert.Equal("invalid", invalid.RootElement.GetProperty("error").GetString());
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        using (var denied = await Execute()) Assert.Equal("executor_read_required", denied.RootElement.GetProperty("reasonCode").GetString());
    }

    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "workspaces.read")] [InlineData(true, "workspaces.read")]
    public async Task Tenant_workspace_model_retained_observations_repeat_current_compound_policy(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, false); await GrantWorkspaceRuntimeReads(f, s);
        var service = WorkspaceRuntimeReads(f); var call = new ModelToolCall("retained-workspace", ToolCatalog.WorkspaceRead, """{"path":"/home/private.txt"}""");
        var text = await service.InspectAsync(frame, call, 16000);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var messages = new ModelMessage[] { new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name) };
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, text.Length, 10000, 100), null), f.Ids.NewId());
        await service.RequireStoredAsync(run);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        Assert.Equal("workspace_read_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.RequireStoredAsync(run).AsTask())).ReasonCode);
        Assert.Null(await new TenantRunExecutionAuthority(Operations(f), f.Iam, f.Clock, f.RunConfiguration(), workspaceReads: service).CurrentDefinitionAsync(run));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_model_withholds_read_or_list_when_authority_changes_during_IO(bool sqlite, bool list)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, true); var grant = await GrantWorkspaceRuntimeReads(f, s);
        var probe = new WorkspaceRuntimeReadProbe(WorkspaceReader(f), async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant,
            grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var call = new ModelToolCall("raced-workspace", list ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead,
            JsonSerializer.Serialize(new { path = list ? "/working" : "/working/private.txt" }));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceRuntimeReads(f, probe).InspectAsync(frame, call, 16000).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Fired);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_model_empty_lists_do_not_initialize_and_bounded_pages_preserve_next_unseen_path(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s); var service = WorkspaceRuntimeReads(f);
        var list = new ModelToolCall("page-workspace", ToolCatalog.WorkspaceList, """{"path":"/working","limit":1}""");
        using (var empty = JsonDocument.Parse(await service.InspectAsync(frame, list, 1000))) Assert.Empty(empty.RootElement.GetProperty("entries").EnumerateArray());
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, s.Run.SessionId)));
        await SeedRuntimeWorkspace(f, s, true, "a.txt"); await SeedRuntimeWorkspace(f, s, true, "b.txt");
        using var first = JsonDocument.Parse(await service.InspectAsync(frame, list, 16000)); var cursor = first.RootElement.GetProperty("nextPath").GetString();
        Assert.Equal("/working/a.txt", cursor);
        using var second = JsonDocument.Parse(await service.InspectAsync(frame, list with { ArgumentsJson = JsonSerializer.Serialize(new { path = "/working", cursor, limit = 1 }) }, 16000));
        Assert.Equal("/working/b.txt", Assert.Single(second.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        using var small = JsonDocument.Parse(await service.InspectAsync(frame, list, 80)); Assert.Equal("output_limit", small.RootElement.GetProperty("error").GetString());
        await SeedRuntimeWorkspace(f, s, true, "binary.dat", [0, 255, 3]);
        using var binary = JsonDocument.Parse(await service.InspectAsync(frame, new("binary", ToolCatalog.WorkspaceRead, """{"path":"/working/binary.dat"}"""), 16000));
        Assert.Equal("unsupported_reader", binary.RootElement.GetProperty("error").GetString());
        using var missing = JsonDocument.Parse(await service.InspectAsync(frame, new("missing", ToolCatalog.WorkspaceRead, """{"path":"/working/missing.txt"}"""), 16000));
        Assert.Equal("workspace_read_unavailable", missing.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(f.Root, missing.RootElement.GetRawText());
    }

    private sealed class WorkspaceRuntimeReadProbe(ITenantWorkspaceReadStore inner, Func<Task> change) : ITenantWorkspaceReadStore
    {
        public int Fired;
        private async Task Changed() { if (Interlocked.Exchange(ref Fired, 1) == 0) await change(); }
        public async ValueTask<AgentWorkspacePage> ListTenantAsync(TenantWorkspaceReference owner, string path, string? cursor, int limit, CancellationToken ct = default)
        { var value = await inner.ListTenantAsync(owner, path, cursor, limit, ct); await Changed(); return value; }
        public ValueTask<AgentWorkspacePage> SearchTenantAsync(TenantWorkspaceReference owner, string query, string? cursor, int limit, CancellationToken ct = default) => throw new InvalidOperationException("This conversion cannot search.");
        public async ValueTask<AgentWorkspaceContent> InspectTenantAsync(TenantWorkspaceReference owner, string path, int maxContentBytes, CancellationToken ct = default)
        { var value = await inner.InspectTenantAsync(owner, path, maxContentBytes, ct); if (maxContentBytes > 0) await Changed(); return value; }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_model_exact_home_read_scope_cannot_read_working_or_borrow_owner_access(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, false); await SeedRuntimeWorkspace(f, s, true);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        var homeGrant = await GrantWorkspaceRuntimeReads(f, s, ["workspaces.read"], TenantWorkspaceRules.Home(s.Tenant, s.Agent.Instance.InstanceId));
        var service = WorkspaceRuntimeReads(f); var home = new ModelToolCall("home-scope", ToolCatalog.WorkspaceRead, """{"path":"/home/private.txt"}""");
        Assert.Contains("tenantWorkspaceRead", await service.InspectAsync(frame, home, 16000));
        var working = home with { ArgumentsJson = """{"path":"/working/private.txt"}""" };
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.InspectAsync(frame, working, 16000).AsTask())).ReasonCode);
        await GrantWorkspaceRuntimeReads(f, s, ["workspaces.read"], TenantWorkspaceRules.Working(s.Tenant, frame.SessionId));
        Assert.Contains("tenantWorkspaceRead", await service.InspectAsync(frame, working, 16000));
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, homeGrant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.InspectAsync(frame, home, 16000).AsTask())).ReasonCode);
        Assert.Contains("tenantWorkspaceRead", await service.InspectAsync(frame, working, 16000));
    }
}
