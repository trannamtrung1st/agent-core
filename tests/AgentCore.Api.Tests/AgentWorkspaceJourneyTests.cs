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
    public async Task Retain_survives_source_delete_checkout_updates_publish_and_archive_is_readonly()
    {
        var client = Owner();
        var a = await CreateInstance(client); var b = await CreateInstance(client);
        var a1 = await CreateSession(client, a);
        var bytes = "# Store review\r\nExact source: café\n"u8.ToArray();
        const string scratch = "/workspace/working/store-review.md"; const string home = "/home/reports/store-review.md";
        (await client.PutAsync($"/api/v2/sessions/{a1}/workspace/content?path={scratch}", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{a}/workspace"))!.Items);
        var response = await client.PostAsJsonAsync($"/api/v2/sessions/{a1}/workspace/retain", new WorkspaceRetainRequest(scratch, home)); response.EnsureSuccessStatusCode();
        var first = (await response.Content.ReadFromJsonAsync<AgentWorkspaceItemResponse>())!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), first.Sha256Hex);
        Assert.Equal(a1, first.SourceSessionId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v2/sessions/{a1}/workspace/retain", new WorkspaceRetainRequest(scratch, home))).StatusCode);
        (await client.DeleteAsync($"/api/v1/sessions/{a1}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/v2/sessions/{a1}")).EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/agent-instances/{a}/workspace/{first.ItemId}/content"));
        var a2 = await CreateSession(client, a);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{a2}/workspace/content?path={home}"));
        var nodes = await client.GetFromJsonAsync<WorkspaceNodeResponse[]>($"/api/v2/sessions/{a2}/workspace?prefix=/home/reports");
        Assert.Contains(nodes!, n => n.LogicalPath == home && !n.Writable);
        var checkout = await client.PostAsJsonAsync($"/api/v2/sessions/{a2}/workspace/checkout", new WorkspaceCheckoutRequest(home, scratch, first.Revision, first.Sha256Hex)); checkout.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{a2}/workspace/content?path={scratch}"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v2/sessions/{a2}/workspace/checkout", new WorkspaceCheckoutRequest(home, scratch))).StatusCode);
        var revised = "# Revised store review\nKeep exact bytes.\n"u8.ToArray();
        (await client.PutAsync($"/api/v2/sessions/{a2}/workspace/content?path={scratch}", new ByteArrayContent(revised))).EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/agent-instances/{a}/workspace/{first.ItemId}/content"));
        var replacement = await client.PostAsJsonAsync($"/api/v2/sessions/{a2}/workspace/retain", new WorkspaceRetainRequest(scratch, home, first.Revision, first.Sha256Hex)); replacement.EnsureSuccessStatusCode();
        var second = (await replacement.Content.ReadFromJsonAsync<AgentWorkspaceItemResponse>())!; Assert.Equal(2, second.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v2/sessions/{a2}/workspace/retain", new WorkspaceRetainRequest(scratch, home, first.Revision))).StatusCode);
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        var snapshot = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(a2))!;
        var search = await executor.ExecuteAsync(snapshot.Definition, a2, new("search", ToolCatalog.WorkspaceSearch, """{"path":"/home","query":"revised"}"""), 8192);
        Assert.Contains(home, search.Text!);
        var artifact = await executor.ExecuteAsync(snapshot.Definition, a2, new("publish", ToolCatalog.ArtifactsCreateFromWorkspace,
            """{"path":"/workspace/working/store-review.md","displayName":"store-review.md"}"""), 8192);
        Assert.DoesNotContain("error", artifact.Text!);
        var records = await client.GetFromJsonAsync<ArtifactResponse[]>($"/api/v2/sessions/{a2}/artifacts");
        Assert.Single(records!); Assert.Equal(revised, await client.GetByteArrayAsync($"/api/v2/sessions/{a2}/artifacts/{records![0].ArtifactId}/content"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{a2}/artifacts/{second.ItemId}/content")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{b}/workspace"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{b}/workspace/{second.ItemId}/content")).StatusCode);
        var archive = await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{a}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived")); archive.EnsureSuccessStatusCode();
        Assert.Equal(revised, await client.GetByteArrayAsync($"/api/v2/agent-instances/{a}/workspace/{second.ItemId}/content"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v2/agent-instances/{a}/workspace/{second.ItemId}?expectedRevision=2")).StatusCode);
        (await client.DeleteAsync($"/api/v2/sessions/{a2}")).EnsureSuccessStatusCode();
        var deleted = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/admin/agent-instances/{a}")
        { Content = JsonContent.Create(new AdminInstanceDeleteRequest(2)) });
        deleted.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{a}/workspace")).StatusCode);
        var store = _factory.Services.GetRequiredService<IAgentInstanceWorkspaceStore>();
        Assert.Empty((await store.ListAsync(a, "/home", null, 10)).Items);
        Assert.NotNull(await _factory.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(b));
    }

    [Fact]
    public async Task Owner_filter_compatibility_paths_and_delete_CAS_are_enforced()
    {
        var client = Owner(); var a = await CreateInstance(client); var session = await CreateSession(client, a);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/v2/agent-instances/{a}/workspace")).StatusCode);
        var legacy = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("general-assistant", 13, "text")); legacy.EnsureSuccessStatusCode();
        var compatibilitySession = (await legacy.Content.ReadFromJsonAsync<SessionViewResponse>())!.SessionId;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v2/sessions/{compatibilitySession}/workspace?prefix=/home")).StatusCode);
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/workspace/working/note.txt", new ByteArrayContent("hello"u8.ToArray()))).EnsureSuccessStatusCode();
        var retained = await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/retain", new WorkspaceRetainRequest("note.txt", "/home/note.txt")); retained.EnsureSuccessStatusCode();
        var item = (await retained.Content.ReadFromJsonAsync<AgentWorkspaceItemResponse>())!;
        var download = await client.GetAsync($"/api/v2/agent-instances/{a}/workspace/{item.ItemId}/content");
        Assert.Contains("note.txt", download.Content.Headers.ContentDisposition!.ToString()); Assert.Equal("\"1\"", download.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v2/agent-instances/{a}/workspace/{item.ItemId}?expectedRevision=9")).StatusCode);
        (await client.DeleteAsync($"/api/v2/agent-instances/{a}/workspace/{item.ItemId}?expectedRevision=1")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{a}/workspace/{item.ItemId}/content")).StatusCode);
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        var snapshot = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!;
        var forged = await executor.ExecuteAsync(snapshot.Definition, session, new("retain", ToolCatalog.WorkspaceRetain,
            $$"""{"source":"note.txt","destination":"/home/a","agentInstanceId":"{{Guid.NewGuid()}}"}"""), 8192);
        Assert.Contains("invalid", forged.Text!);
        var denied = await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/note.txt", new ByteArrayContent("bad"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var invalid = await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/retain", new WorkspaceRetainRequest("note.txt", "/home/../secret"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Native_structure_checks_exact_approval_generation_scope_and_archive()
    {
        var client = Owner(); var owner = await CreateInstance(client, 14); var session = await CreateSession(client, owner);
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=inbox/report.txt", new ByteArrayContent("exact"u8.ToArray()))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/retain", new WorkspaceRetainRequest("inbox/report.txt", "/home/inbox/report.txt"))).EnsureSuccessStatusCode();
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
        Assert.Contains("Forbidden",(await executor.ExecuteAsync(definition,session,new("cross",ToolCatalog.WorkspaceCopy,"""{"source":"/home/inbox/report.txt","destination":"/workspace/working/leak"}"""),8192)).Text!);
        foreach(var args in new[] { """{"path":"/home/test","agentInstanceId":"spoof"}""", """{"path":"/home/../escape"}""", """{"path":"/home/.env"}""", """{"path":"/home/a","path":"/home/b"}""" })
            Assert.Contains("error",(await executor.ExecuteAsync(definition,session,new("bad",ToolCatalog.WorkspaceMkdir,args),8192)).Text!);
        (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{owner}/lifecycle",new AdminUpdateAgentInstanceLifecycleRequest(1,"Archived"))).EnsureSuccessStatusCode();
        current = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!;
        Assert.Contains("Conflict",(await executor.ExecuteAsync(definition,session,new("mkdir",ToolCatalog.WorkspaceMkdir,System.Text.Json.JsonSerializer.Serialize(new {path="/home/archived",expectedTreeSha256=current.TreeSha256})),8192)).Text!);
    }

    [Fact]
    public async Task Home_tool_listing_does_not_hide_later_folders_after_a_large_first_folder()
    {
        var client = Owner(); var owner = await CreateInstance(client, 14); var session = await CreateSession(client, owner);
        var store = _factory.Services.GetRequiredService<IAgentInstanceWorkspaceStore>();
        for (var i=0; i<270; i++) await store.RetainAsync(owner, $"/home/a/file{i:D3}.txt", "text/plain", "x"u8.ToArray(), null, null, null);
        await store.RetainAsync(owner, "/home/z/last.txt", "text/plain", "last"u8.ToArray(), null, null, null);
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

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public async Task Move_authority_requires_structural_opt_in_without_widening_older_definitions(int version)
    {
        var client = Owner(); var owner = await CreateInstance(client, version); var session = await CreateSession(client, owner);
        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var structural = version == 14;
        Assert.Equal(structural, WorkspaceFilesystemPolicy.AllowsStructure(definition));
        Assert.Equal(structural, WorkspaceFilesystemPolicy.AllowsStructure(definition with { Version = 999 }));
        var offered = Assert.Single(ToolCatalog.For(definition, null, _factory.Services.GetRequiredService<IToolConfigurationGate>()), t => t.Name == ToolCatalog.WorkspaceMove);
        Assert.Equal(structural, offered.ParametersJson.Contains("expectedTreeSha256", StringComparison.Ordinal));
        Assert.Equal(structural, offered.Description.Contains("directory", StringComparison.Ordinal));
        var executor = _factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<string> Move(string source, string destination, string? token = null) =>
            (await executor.ExecuteAsync(definition, session, new("move", ToolCatalog.WorkspaceMove,
                System.Text.Json.JsonSerializer.Serialize(new { source, destination, expectedTreeSha256 = token },
                    new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })), 8192)).Text!;
        (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=draft.txt", new ByteArrayContent("file"u8.ToArray()))).EnsureSuccessStatusCode();
        using var fileMove = System.Text.Json.JsonDocument.Parse(await Move("draft.txt", "reports/draft.txt"));
        Assert.False(fileMove.RootElement.TryGetProperty("error", out _));
        Assert.Equal(structural, fileMove.RootElement.TryGetProperty("completed", out _));
        Assert.Equal("file"u8.ToArray(), await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=reports/draft.txt"));
        var treeMove = await Move("reports", "renamed");
        Assert.Equal(!structural, treeMove.Contains("Forbidden", StringComparison.Ordinal));
        Assert.Equal("file"u8.ToArray(), await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path={(structural ? "renamed" : "reports")}/draft.txt"));
        var store = _factory.Services.GetRequiredService<IAgentInstanceWorkspaceStore>();
        await store.RetainAsync(owner, "/home/project/kept.txt", "text/plain", "durable"u8.ToArray(), session, null, null);
        var before = await store.ListAsync(owner, "/home", null, 256);
        var homeMove = await Move("/home/project", "/home/moved", before.TreeSha256);
        Assert.Equal(!structural, homeMove.Contains("Forbidden", StringComparison.Ordinal));
        var fileSource = structural ? "/home/moved/kept.txt" : "/home/project/kept.txt";
        var current = await store.ListAsync(owner, "/home", null, 256);
        Assert.Equal(!structural, (await Move(fileSource, "/home/renamed.txt", current.TreeSha256)).Contains("Forbidden", StringComparison.Ordinal));
        Assert.Equal("durable"u8.ToArray(), (await store.ReadAsync(owner, null, structural ? "/home/renamed.txt" : fileSource)).Bytes);
        if (!structural) Assert.Equal(before.TreeSha256, (await store.ListAsync(owner, "/home", null, 256)).TreeSha256);
    }

    [Fact]
    public async Task V2_managed_workspace_uses_cwd_direct_CAS_tree_copy_and_home_artifacts()
    {
        var client = Owner(); var owner = await CreateInstance(client, 15); var session = await CreateSession(client, owner);
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
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary-tree/raw.bin"));
        Assert.Equal("Conflict", (await Tool(ToolCatalog.WorkspaceCopy, new { source = "/home/project", destination = "/working/copy" })).GetProperty("error").GetString());
        Assert.Equal("Forbidden", (await Tool(ToolCatalog.WorkspaceMove, new { source = "/working/copy", destination = "/home/transferred" })).GetProperty("error").GetString());
        await Tool(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/working" });
        await Tool(ToolCatalog.WorkspaceWrite, new { path = "scratch.md", content = "scratch" });
        Assert.Equal("scratch", System.Text.Encoding.UTF8.GetString(await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/scratch.md")));
        var artifact = await Tool(ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/project/notes.md", displayName = "notes.md" });
        Assert.Equal("updated café", System.Text.Encoding.UTF8.GetString(await client.GetByteArrayAsync($"/api/v2/sessions/{session}/artifacts/{artifact.GetProperty("artifactId").GetString()}/content")));
        Assert.Equal("forbidden", (await Tool(ToolCatalog.WorkspaceRetain, new { source = "/working/scratch.md", destination = "/home/legacy.md" })).GetProperty("error").GetString());
        var fresh = await CreateSession(client, owner);
        var result = await executor.ExecuteAsync(snapshot.Definition, fresh, new("new", ToolCatalog.WorkspaceCwd, "{\"operation\":\"get\"}"), 8192);
        Assert.Contains("/home", result.Text);
        (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/home/binary-tree/raw.bin"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/scratch.md")).StatusCode);
        var other = await CreateInstance(client, 15); var otherSession = await CreateSession(client, other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{otherSession}/workspace/content?path=/home/project/notes.md")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/{session}/scratch.md")).StatusCode);
    }

    private static async Task<Guid> CreateInstance(HttpClient client, int version = 13)
    {
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new AdminCreateAgentInstanceRequest("general-assistant", version)); response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId);
    }
    private static async Task<Guid> CreateSession(HttpClient client, Guid owner)
    {
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(null, null, "text", AgentInstanceId: owner)); response.EnsureSuccessStatusCode();
        return Guid.Parse((await response.Content.ReadFromJsonAsync<SessionViewResponse>())!.SessionId);
    }

    [Fact]
    public async Task Sqlite_host_reopen_preserves_retained_bytes_after_source_deletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "home-api-restart", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid a; AgentWorkspaceItemResponse item;
        var bytes = "SQLite host restart: exact bytes\r\n"u8.ToArray();
        try
        {
            using (var first = new SqliteFactory(root))
            {
                var client = OwnerOf(first); a = await CreateInstance(client); var session = await CreateSession(client, a);
                (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=source.txt", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
                var retain = await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/retain", new WorkspaceRetainRequest("source.txt", "/home/restart.txt")); retain.EnsureSuccessStatusCode();
                item = (await retain.Content.ReadFromJsonAsync<AgentWorkspaceItemResponse>())!;
                (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
            }
            using (var second = new SqliteFactory(root))
            {
                var client = OwnerOf(second);
                var page = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{a}/workspace"))!;
                Assert.Equal(item, Assert.Single(page.Items));
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/agent-instances/{a}/workspace/{item.ItemId}/content"));
                var session = await CreateSession(client, a);
                (await client.PostAsJsonAsync($"/api/v2/sessions/{session}/workspace/checkout", new WorkspaceCheckoutRequest(item.LogicalPath))).EnsureSuccessStatusCode();
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=restart.txt"));
                (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
                (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{a}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"))).EnsureSuccessStatusCode();
                (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/admin/agent-instances/{a}") { Content = JsonContent.Create(new AdminInstanceDeleteRequest(2)) })).EnsureSuccessStatusCode();
                Assert.False(Directory.Exists(Path.Combine(root, "home", a.ToString("N"))));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task V2_Sqlite_reopen_preserves_home_and_owner_nested_scratch_then_cleans_each_lifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "workspace-v2-restart", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid owner, session; byte[] bytes = [0, 255, 128, 13, 10];
        try
        {
            using (var first = new SqliteFactory(root))
            {
                var client = OwnerOf(first); owner = await CreateInstance(client, 15); session = await CreateSession(client, owner);
                (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
                (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/binary.dat", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
                Assert.True(Directory.Exists(Path.Combine(root, "scratch", "agent-" + owner.ToString("N"), "sessions", "session-" + session.ToString("N"), "workspace", "working")));
            }
            using (var second = new SqliteFactory(root))
            {
                var client = OwnerOf(second);
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat"));
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{session}/workspace/content?path=/working/binary.dat"));
                Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path=/home/binary.dat", new ByteArrayContent([42]))).StatusCode);
                (await client.DeleteAsync($"/api/v2/sessions/{session}")).EnsureSuccessStatusCode();
                Assert.False(Directory.Exists(Path.Combine(root, "scratch", "agent-" + owner.ToString("N"), "sessions", "session-" + session.ToString("N"))));
                var fresh = await CreateSession(client, owner);
                Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/home/binary.dat"));
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{fresh}/workspace/content?path=/working/binary.dat")).StatusCode);
                (await client.DeleteAsync($"/api/v2/sessions/{fresh}")).EnsureSuccessStatusCode();
                (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{owner}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"))).EnsureSuccessStatusCode();
                (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/admin/agent-instances/{owner}") { Content = JsonContent.Create(new AdminInstanceDeleteRequest(2)) })).EnsureSuccessStatusCode();
                Assert.False(Directory.Exists(Path.Combine(root, "home", owner.ToString("N"))));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static HttpClient OwnerOf(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services)); return client;
    }
    private sealed class SqliteFactory(string root) : DurableSqliteHostFactory(Path.Combine(root, "store.db"))
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
            ["Persistence:Provider"] = "Sqlite", ["Persistence:ConnectionString"] = $"Data Source={root}/store.db",
            ["Persistence:WorkspaceRoot"] = Path.Combine(root, "scratch"), ["Persistence:ArtifactRoot"] = Path.Combine(root, "artifacts"),
            ["Persistence:AgentWorkspaceRoot"] = Path.Combine(root, "home"), ["Persistence:AttachmentRoot"] = Path.Combine(root, "attachments"),
            ["Persistence:DefinitionResourceRoot"] = Path.Combine(root, "definition-resources")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISessionWorkspace>();
                services.AddSingleton<ISessionWorkspace>(sp => new FileSessionWorkspace(Path.Combine(root, "scratch"), Path.Combine(root, "templates"), sessions: sp.GetRequiredService<IMemoryStore>()));
                services.RemoveAll<IAgentInstanceWorkspaceStore>();
                services.AddSingleton<IAgentInstanceWorkspaceStore>(sp => new FileAgentInstanceWorkspaceStore(Path.Combine(root, "home"),
                    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
                services.RemoveAll<IAdminLifecycleDeletion>();
                services.AddSingleton<IAdminLifecycleDeletion>(sp => new SqliteAdminLifecycleDeletion(sp.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<IAgentInstanceWorkspaceStore>()));
            });
        }
    }
}
