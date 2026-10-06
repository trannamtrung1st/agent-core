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

    private static async Task<Guid> CreateInstance(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new AdminCreateAgentInstanceRequest("general-assistant", 13)); response.EnsureSuccessStatusCode();
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
