using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Admin;
using Microsoft.Extensions.Configuration;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Api.Tests;

public sealed class AgentWorkspaceJourneyTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;
    public AgentWorkspaceJourneyTests(AgentCoreApiFactory factory) { _factory = factory; }
    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(_factory.Services)); return client;
    }

    [Fact]
    public async Task Owner_filter_removed_transfer_routes_and_delete_CAS_are_enforced()
    {
        var client = Owner(); var owner = await CreateInstance(client); var session = await CreateSession(client, owner);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/v2/agent-instances/{owner}/workspace")).StatusCode);
        foreach (var removed in new[] { "retain", "checkout" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/{removed}", new { })).StatusCode);
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/note.txt", new ByteArrayContent("hello"u8.ToArray()))).EnsureSuccessStatusCode();
        var item = Assert.Single((await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!.Items, i => !i.Directory);
        var download = await client.GetAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}/content");
        Assert.Contains("note.txt", download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("\"1\"", download.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}?expectedRevision=9")).StatusCode);
        (await client.DeleteAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}?expectedRevision=1")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/../secret", new ByteArrayContent([1]))).StatusCode);
    }

    [Fact]
    public async Task Native_structure_checks_exact_approval_generation_scope_and_archive()
    {
        var client = Owner(); var owner = await CreateInstance(client, 21); var session = await CreateSession(client, owner);
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/inbox/report.txt", new ByteArrayContent("exact"u8.ToArray()))).EnsureSuccessStatusCode();
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var before = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!;
        Assert.Contains(before.Items, i => i.Directory && i.LogicalPath == "/home/inbox");
        var arguments = System.Text.Json.JsonSerializer.Serialize(new {
            operations = new[] { new { op="mkdir",path="/home/projects" }, new { op="mkdir",path="/home/empty" } }, expectedTreeSha256=before.TreeSha256 });
        var call = new ModelToolCall("structure", ToolCatalog.WorkspaceBatch, arguments);
        Assert.Contains("approval_required", (await executor.ExecuteAsync(definition,session,call,8192)).Text!);
        using var json = System.Text.Json.JsonDocument.Parse(arguments);
        var grant = new ToolApprovalGrant(Guid.NewGuid(),call.Name,ToolActionHash.Compute(call.Name,json.RootElement),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        Assert.Contains("approval", (await executor.ExecuteAsync(definition,session,call with { ArgumentsJson=arguments.Replace("projects","changed") },8192,approvalGrant:grant)).Text!);
        Assert.Contains("\"completedCount\":2", (await executor.ExecuteAsync(definition,session,call,8192,approvalGrant:grant)).Text!);
        Assert.Contains("Conflict", (await executor.ExecuteAsync(definition,session,call,8192,approvalGrant:grant)).Text!);
        var current = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!;
        var copied = await executor.ExecuteAsync(definition,session,new("copy",ToolCatalog.WorkspaceCopy,System.Text.Json.JsonSerializer.Serialize(new {source="/home/inbox",destination="/home/projects/report",expectedTreeSha256=current.TreeSha256})),8192);
        Assert.Contains("\"completed\":true",copied.Text!);
        Assert.Equal("exact"u8.ToArray(),await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/projects/report/report.txt"));
        Assert.Contains("completed",(await executor.ExecuteAsync(definition,session,new("cross",ToolCatalog.WorkspaceCopy,"""{"source":"/home/inbox/report.txt","destination":"/working/leak"}"""),8192)).Text!);
        foreach(var args in new[] { """{"path":"/home/test","agentInstanceId":"spoof"}""", """{"path":"/home/../escape"}""", """{"path":"/home/.env"}""", """{"path":"/home/a","path":"/home/b"}""" })
            Assert.Contains("error",(await executor.ExecuteAsync(definition,session,new("bad",ToolCatalog.WorkspaceMkdir,args),8192)).Text!);
        (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{owner}/lifecycle",new AdminUpdateAgentInstanceLifecycleRequest(1,"Archived"))).EnsureSuccessStatusCode();
        current = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!;
        Assert.Contains("Conflict",(await executor.ExecuteAsync(definition,session,new("mkdir",ToolCatalog.WorkspaceMkdir,System.Text.Json.JsonSerializer.Serialize(new {path="/home/archived",expectedTreeSha256=current.TreeSha256})),8192)).Text!);
    }

    [Fact]
    public async Task Home_tool_listing_does_not_hide_later_folders_after_a_large_first_folder()
    {
        var client = Owner(); var owner = await CreateInstance(client, 21); var session = await CreateSession(client, owner);
        var store = _factory.Services.GetRequiredService<IAgentInstanceWorkspaceStore>();
        for (var i=0; i<270; i++) await store.WriteFileAsync(owner, $"/home/a/file{i:D3}.txt", "text/plain", "x"u8.ToArray(), null, null, null);
        await store.WriteFileAsync(owner, "/home/z/last.txt", "text/plain", "last"u8.ToArray(), null, null, null);
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        using var result = System.Text.Json.JsonDocument.Parse((await executor.ExecuteAsync(definition,session,new("list",ToolCatalog.WorkspaceList,"""{"path":"/home"}"""),8192)).Text!);
        var paths = result.RootElement.GetProperty("entries").EnumerateArray().Select(e=>e.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(["/home/a","/home/z"], paths);
        Assert.Equal((await store.ListAsync(owner,"/home",null,1)).TreeSha256,result.RootElement.GetProperty("treeSha256").GetString());
        var copy = await executor.ExecuteAsync(definition,session,new("copy-many",ToolCatalog.WorkspaceCopy,System.Text.Json.JsonSerializer.Serialize(new {source="/home/a",destination="/home/copied",expectedTreeSha256=result.RootElement.GetProperty("treeSha256").GetString()})),8192);
        Assert.Contains("\"filesAffected\":270",copy.Text!);
        using var copiedResult = System.Text.Json.JsonDocument.Parse(copy.Text!);
        Assert.Equal("/home/a",copiedResult.RootElement.GetProperty("operations")[0].GetProperty("source").GetString());
        Assert.Equal(64,copiedResult.RootElement.GetProperty("treeSha256").GetString()!.Length);
        Assert.Equal("x"u8.ToArray(),await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/copied/file269.txt"));
    }

    [Fact]
    public async Task Managed_workspace_uses_cwd_direct_CAS_tree_copy_and_home_artifacts()
    {
        var client = Owner(); var owner = await CreateInstance(client, 21); var session = await CreateSession(client, owner);
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        var snapshot = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!;
        string cwd = "/home";
        async Task<System.Text.Json.JsonElement> Tool(string name, object args, bool approved = false)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(args);
            var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            var grant = approved ? new ToolApprovalGrant(Guid.NewGuid(), name, ToolActionHash.Compute(name, element), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) : null;
            var result = await executor.ExecuteAsync(snapshot.Definition, session, new("v2-test", name, json), 16000,
                approvalGrant: grant, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner, WorkspaceCwd: cwd));
            if (result.WorkspaceCwd is not null) cwd = result.WorkspaceCwd;
            Assert.DoesNotContain("/workspace", result.Text);
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Text);
        }
        var initial = await Tool(ToolCatalog.WorkspaceCwd, new { operation = "get" }); Assert.Equal("/home", initial.GetProperty("cwd").GetString());
        var listing = await Tool(ToolCatalog.WorkspaceList, new { });
        await Tool(ToolCatalog.WorkspaceMkdir, new { path = "project/empty", expectedTreeSha256 = listing.GetProperty("treeSha256").GetString() });
        await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/home/project" }); Assert.Equal("/home/project", cwd);
        var written = await Tool(ToolCatalog.WorkspaceWrite, new { path = "notes.md", content = "hello café" });
        Assert.Equal("/home/project/notes.md", written.GetProperty("path").GetString());
        var metadata = written.GetProperty("homeItem"); var hash = metadata.GetProperty("Sha256Hex").GetString();
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspaceWrite, new { path = "notes.md", content = "lost" })).GetProperty("error").GetString());
        var patch = await Tool(ToolCatalog.WorkspacePatch, new { path = "notes.md", expectedSha256 = hash, edits = new[] { new { oldText = "hello", newText = "updated" } } });
        Assert.True(patch.TryGetProperty("newSha256", out _));
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspacePatch, new { path = "notes.md", expectedSha256 = hash, edits = new[] { new { oldText = "updated", newText = "lost" } } })).GetProperty("error").GetString());
        Assert.Contains("updated", (await Tool(ToolCatalog.WorkspaceRead, new { path = "notes.md" })).GetProperty("content").GetString());
        Assert.Contains("/home/project/notes.md", (await Tool(ToolCatalog.WorkspaceSearch, new { query = "updated" })).GetRawText());
        foreach (var name in new[] { ToolCatalog.WorkspaceDelete, ToolCatalog.WorkspaceBatch })
        {
            var json = name == ToolCatalog.WorkspaceDelete ? "{\"path\":\"notes.md\"}" : "{\"operations\":[{\"op\":\"delete\",\"path\":\"notes.md\"}]}";
            var args = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            var prepared = await ToolActionPreparation.PrepareApprovalAsync(executor, new("preview", name, json), args,
                definition: snapshot.Definition, sessionId: session, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner, WorkspaceCwd: cwd));
            Assert.Null(prepared.ErrorJson);
            Assert.Equal(ToolActionHash.Compute(name, args), prepared.Preparation!.ActionHash);
            Assert.Equal(json, prepared.Preparation.ActionJson);
            Assert.Contains("/home/project/notes.md", prepared.Preparation.Details!["Exact operations"]);
            Assert.DoesNotContain("/workspace", prepared.Preparation.Details["Path scope"]);
        }
        Assert.Equal("notFound", (await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "missing" })).GetProperty("error").GetString());
        Assert.Equal("invalid", (await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "notes.md" })).GetProperty("error").GetString());
        Assert.True((await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/agent" })).TryGetProperty("error", out _));
        listing = await Tool(ToolCatalog.WorkspaceList, new { path = "/home" });
        var treeToken = listing.GetProperty("treeSha256").GetString();
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspaceMove, new { source = "/home/project", destination = "/home/renamed", expectedTreeSha256 = treeToken })).GetProperty("error").GetString());
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspaceDelete, new { path = "/home/project", recursive = true, expectedTreeSha256 = treeToken }, true)).GetProperty("error").GetString());
        await Tool(ToolCatalog.WorkspaceCopy, new { source = "/home/project", destination = "/working/copy" });
        var scratch = await client.GetFromJsonAsync<WorkspaceNodeResponse[]>($"/api/v2/sessions/{session}/workspace?prefix=/working/copy");
        Assert.Contains(scratch!, n => n.LogicalPath == "/working/copy/empty" && n.Directory);
        var bytes = new byte[] { 0, 255, 128, 13, 10 };
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/copy/raw.bin", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        await Tool(ToolCatalog.WorkspaceCopy, new { source = "/working/copy", destination = "/home/binary-tree" });
        var homeStore = _factory.Services.GetRequiredService<IAgentInstanceWorkspaceStore>();
        Assert.All((await homeStore.ListAsync(owner, "/home/binary-tree", null, 256)).Items,
            item => Assert.Equal(session, item.SourceSessionId));
        await Tool(ToolCatalog.WorkspaceCopy, new { source = "/working/copy/raw.bin", destination = "/home/single.bin" });
        var copiedFile = (await homeStore.ReadAsync(owner, null, "/home/single.bin")).Item;
        Assert.Equal(session, copiedFile.SourceSessionId);
        await Tool(ToolCatalog.WorkspaceCopy, new { source = "/working/copy/raw.bin", destination = "/home/single.bin", expectedRevision = copiedFile.Revision });
        Assert.Equal(session, (await homeStore.ReadAsync(owner, null, "/home/single.bin")).Item.SourceSessionId);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary-tree/raw.bin"));
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspaceCopy, new { source = "/home/project", destination = "/working/copy" })).GetProperty("error").GetString());
        Assert.Equal("Forbidden", (await Tool(ToolCatalog.WorkspaceMove, new { source = "/working/copy", destination = "/home/transferred" })).GetProperty("error").GetString());
        await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/working" });
        await Tool(ToolCatalog.WorkspaceWrite, new { path = "scratch.md", content = "scratch" });
        const string deleteJson = "{\"path\":\"scratch.md\"}";
        var deleteArgs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(deleteJson);
        var deletePreview = await ToolActionPreparation.PrepareApprovalAsync(executor, new("preview", ToolCatalog.WorkspaceDelete, deleteJson), deleteArgs,
            definition: snapshot.Definition, sessionId: session, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner, WorkspaceCwd: cwd));
        Assert.Contains("/working/scratch.md", deletePreview.Preparation!.Details!["Exact operations"]);
        Assert.DoesNotContain("/workspace", deletePreview.Preparation.Details["Exact operations"]);
        Assert.Equal("scratch", System.Text.Encoding.UTF8.GetString(await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/scratch.md")));
        var artifact = await Tool(ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/project/notes.md", displayName = "notes.md" });
        Assert.Equal("updated café", System.Text.Encoding.UTF8.GetString(await client.GetByteArrayAsync($"/api/v2/sessions/{session}/artifacts/{artifact.GetProperty("artifactId").GetString()}/content")));
        Assert.Equal("forbidden", (await Tool("workspace.retain", new { source = "/working/scratch.md", destination = "/home/legacy.md" })).GetProperty("error").GetString());
        var fresh = await CreateSession(client, owner);
        var result = await executor.ExecuteAsync(snapshot.Definition, fresh, new("new", ToolCatalog.WorkspaceCwd, "{\"operation\":\"get\"}"), 8192);
        Assert.Contains("/home", result.Text);
        (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/home/binary-tree/raw.bin"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/scratch.md")).StatusCode);
        var other = await CreateInstance(client, 21); var otherSession = await CreateSession(client, other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{otherSession}/workspace/content?path=/home/project/notes.md")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/{session}/scratch.md")).StatusCode);
    }

    private static async Task<Guid> CreateInstance(HttpClient client, int version = 21)
    {
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new AdminCreateAgentInstanceRequest("general-assistant", version)); response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId);
    }
    private static async Task<Guid> CreateSession(HttpClient client, Guid owner)
    {
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(owner, "text")); response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<SessionViewResponse>())!.SessionId);
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_home_and_owner_nested_scratch_then_cleans_each_lifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "workspace-v2-restart", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid owner, session; byte[] bytes = [0, 255, 128, 13, 10];
        try
        {
            using (var first = new SqliteFactory(root))
            {
                var client = OwnerOf(first); owner = await CreateInstance(client, 21); session = await CreateSession(client, owner);
                (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
                (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/binary.dat", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
                Assert.True(Directory.Exists(AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), owner, session)));
            }
            using (var second = new SqliteFactory(root))
            {
                var client = OwnerOf(second);
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat"));
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/binary.dat"));
                Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat", new ByteArrayContent([42]))).StatusCode);
                (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
                Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.SessionRoot(Path.Combine(root, "workspaces"), owner, session)));
                var fresh = await CreateSession(client, owner);
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/home/binary.dat"));
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/binary.dat")).StatusCode);
                (await client.DeleteAsync($"/api/v2/sessions/{fresh}")).EnsureSuccessStatusCode();
                (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{owner}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"))).EnsureSuccessStatusCode();
                (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/admin/agent-instances/{owner}") { Content = JsonContent.Create(new AdminInstanceDeleteRequest(2)) })).EnsureSuccessStatusCode();
                Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(root, "workspaces"), owner)));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static HttpClient OwnerOf(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services)); return client;
    }
    [Fact]
    public async Task Sqlite_host_startup_purges_bytes_left_after_committed_logical_deletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "workspace-cleanup-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Guid owner;
        try
        {
            using (var first = new SqliteFactory(root))
            {
                var client = OwnerOf(first); owner = await CreateInstance(client, 21);
                var services = first.Services;
                await services.GetRequiredService<IAgentInstanceWorkspaceStore>().WriteFileAsync(owner,
                    "/home/leftover.bin", "application/octet-stream", new byte[] { 0, 255, 128 }, null, null, null);
                (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{owner}/lifecycle",
                    new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"))).EnsureSuccessStatusCode();
                // Simulate exit in the commit-to-purge gap: commit deletion with no physical adapter.
                var logicalOnly = new SqliteAdminLifecycleDeletion(services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    services.GetRequiredService<IIdGenerator>());
                await logicalOnly.DeleteInstanceAsync(new(owner, 2, Guid.NewGuid(), DateTimeOffset.UtcNow));
                Assert.True(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(root, "workspaces"), owner)));
            }
            var cleanupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var restarted = new SqliteFactory(root, cleanupComplete);
            var restartedClient = OwnerOf(restarted);
            await cleanupComplete.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(root, "workspaces"), owner)));
            Assert.Equal(HttpStatusCode.NotFound,
                (await restartedClient.GetAsync($"/api/v2/agent-instances/{owner}/workspace")).StatusCode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class ObservedDeletion(IAdminLifecycleDeletion inner, TaskCompletionSource completed) : IAdminLifecycleDeletion
    {
        public ValueTask DeleteInstanceAsync(AgentCore.Application.Admin.AdminInstanceDeleteCommand command, CancellationToken ct = default) => inner.DeleteInstanceAsync(command, ct);
        public ValueTask DeleteDefinitionAsync(AgentCore.Application.Admin.AdminDefinitionDeleteCommand command, CancellationToken ct = default) => inner.DeleteDefinitionAsync(command, ct);
        public async ValueTask RecoverWorkspaceCleanupAsync(CancellationToken ct = default)
        {
            await inner.RecoverWorkspaceCleanupAsync(ct);
            completed.TrySetResult();
        }
    }

    private sealed class SqliteFactory(string root, TaskCompletionSource? cleanupComplete = null) : DurableSqliteHostFactory(Path.Combine(root, "store.db"))
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
            ["Persistence:Provider"] = "Sqlite", ["Persistence:ConnectionString"] = $"Data Source={root}/store.db",
            ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces"), ["Persistence:ArtifactRoot"] = Path.Combine(root, "artifacts"),
            ["Persistence:AttachmentRoot"] = Path.Combine(root, "attachments"),
            ["Persistence:DefinitionResourceRoot"] = Path.Combine(root, "definition-resources")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISessionWorkspace>();
                services.AddSingleton<ISessionWorkspace>(sp => new FileSessionWorkspace(Path.Combine(root, "workspaces"), Path.Combine(root, "templates"), sessions: sp.GetRequiredService<IMemoryStore>()));
                services.RemoveAll<IAgentInstanceWorkspaceStore>();
                services.AddSingleton<IAgentInstanceWorkspaceStore>(sp => new FileAgentInstanceWorkspaceStore(Path.Combine(root, "workspaces"),
                    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
                services.RemoveAll<IAdminLifecycleDeletion>();
                services.AddSingleton<IAdminLifecycleDeletion>(sp =>
                {
                    var deletion = new SqliteAdminLifecycleDeletion(sp.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                        sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<IAgentInstanceWorkspaceStore>());
                    return cleanupComplete is null ? deletion : new ObservedDeletion(deletion, cleanupComplete);
                });
            });
        }
    }
}
