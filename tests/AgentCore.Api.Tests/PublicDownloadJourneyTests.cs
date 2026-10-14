using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.PublicWeb;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class PublicDownloadJourneyTests
{
    [Fact]
    public async Task Public_original_docx_download_inspect_fill_publish_and_readback_preserves_template_parts()
    {
        using var host = new Factory();
        using var client = Owner(host);
        var (owner, session) = await Create(client);
        var discovered = await Tool(host.Services, owner, session, ToolCatalog.WebSearch, new { query = "official public application Word form", limit = 1 }); Success(discovered);
        var sourceUrl = discovered.GetProperty("results")[0].GetProperty("url").GetString();
        Assert.Equal("https://official.example/forms/application.docx", sourceUrl);
        var office = host.Services.GetRequiredService<IOfficeProcessor>();
        var original = office.CreateDocument(new("Official template fixture", [new("Applicant", ["Name: ______", "Unchanged instructions"], [["Field", "Value"], ["Date", "2026-10-14"]])]), default).Bytes;
        host.Transport.Responses.Enqueue(new(200, null, "application/octet-stream", original));
        var downloaded = await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = sourceUrl, path = "/home/forms/application.docx" });
        Success(downloaded); Assert.Equal("completed", downloaded.GetProperty("status").GetString()); Assert.Equal(Hash(original), downloaded.GetProperty("sha256").GetString());
        var stored = await host.Services.GetRequiredService<IAgentInstanceWorkspaceStore>().ReadAsync(owner, null, "/home/forms/application.docx");
        Assert.Equal(original, stored.Bytes); Assert.DoesNotContain(Convert.ToBase64String(original), downloaded.ToString());
        var inspected = await Tool(host.Services, owner, session, ToolCatalog.OfficeDocumentInspect, new { path = "/home/forms/application.docx" }); Success(inspected);
        var selector = inspected.GetProperty("inspection").GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("text").GetString() == "Name: ______").GetProperty("selector").GetString();
        var edited = await Tool(host.Services, owner, session, ToolCatalog.OfficeDocumentEdit, new { path = "/home/forms/application.docx", expectedSha256 = Hash(original), operations = new[] { new { operation = "replace_paragraph", selector, text = "Name: Nguyen An" } } });
        Success(edited); Assert.Contains("run formatting is normalized", edited.GetProperty("warnings").ToString());
        var filled = await host.Services.GetRequiredService<IAgentInstanceWorkspaceStore>().ReadAsync(owner, null, "/home/forms/application.docx");
        Assert.Equal(ZipPart(original, "word/styles.xml"), ZipPart(filled.Bytes, "word/styles.xml"));
        var after = await Tool(host.Services, owner, session, ToolCatalog.OfficeDocumentInspect, new { path = "/home/forms/application.docx" });
        Assert.Contains("Name: Nguyen An", after.ToString()); Assert.Contains("Unchanged instructions", after.ToString()); Assert.Contains("2026-10-14", after.ToString());
        var artifact = await Tool(host.Services, owner, session, ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/forms/application.docx", displayName = "completed.docx" }); Success(artifact);
        var readback = await client.GetAsync($"/api/v2/sessions/{session}/artifacts/{artifact.GetProperty("artifactId").GetString()}/content"); readback.EnsureSuccessStatusCode();
        Assert.Equal(filled.Bytes, await readback.Content.ReadAsByteArrayAsync());
        var preview = await client.GetAsync($"/api/v2/sessions/{session}/artifacts/{artifact.GetProperty("artifactId").GetString()}/office-preview"); preview.EnsureSuccessStatusCode(); Assert.Equal(Hash(filled.Bytes), (await preview.Content.ReadFromJsonAsync<OfficePreviewResponse>())!.Sha256);
        Assert.Equal(1, host.Transport.Calls);
    }

    [Fact]
    public async Task Binary_xlsx_doc_pdf_image_archive_and_unknown_formats_are_exact_and_scope_isolated()
    {
        using var host = new Factory(); using var client = Owner(host); var (owner, session) = await Create(client);
        var office = host.Services.GetRequiredService<IOfficeProcessor>();
        var workbook = office.CreateWorkbook(new([new("Form", [[new("text", "Original workbook")]])]), default).Bytes;
        var bodies = new (string Name, string? Mime, byte[] Bytes)[] {
            ("source.xlsx", null, workbook), ("legacy.doc", "text/plain", [0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1,0,255]),
            ("source.pdf", "application/octet-stream", "%PDF-1.7\nfixture"u8.ToArray()),
            ("source.png", null, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")), ("source.zip", null, workbook), ("source.custom", "made/up", [0,255,128,1,2,3]) };
        foreach (var body in bodies)
        {
            host.Transport.Responses.Enqueue(new(200, null, body.Mime, body.Bytes));
            var result = await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = "https://official.example/" + body.Name, path = "/home/" + body.Name }); Success(result);
            Assert.Equal(Hash(body.Bytes), result.GetProperty("sha256").GetString()); Assert.Equal(body.Bytes, (await host.Services.GetRequiredService<IAgentInstanceWorkspaceStore>().ReadAsync(owner, null, "/home/" + body.Name)).Bytes);
        }
        var sheet = await Tool(host.Services, owner, session, ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/source.xlsx" }); Success(sheet); Assert.Contains("Original workbook", sheet.ToString());
        var legacy = await Tool(host.Services, owner, session, ToolCatalog.OfficeDocumentInspect, new { path = "/home/legacy.doc" }); Assert.Contains("invalid_office_package", legacy.ToString());
        host.Transport.Responses.Enqueue(new(200, null, null, [0,255,123]));
        Success(await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = "https://official.example/scratch.bin", path = "/working/scratch.bin" }));
        var (_, next) = await Create(client, owner);
        Assert.Equal(workbook, await (await client.GetAsync($"/api/v2/sessions/{next}/workspace/content?path=/home/source.xlsx")).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{next}/workspace/content?path=/working/scratch.bin")).StatusCode);
        var (otherOwner, otherSession) = await Create(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{otherSession}/workspace/content?path=/home/source.xlsx")).StatusCode);
        Assert.Contains("forbidden", (await Tool(host.Services, otherOwner, session, ToolCatalog.WebDownload, new { url = "https://official.example/x", path = "/home/foreign.bin" })).ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bodies.Length + 1, host.Transport.Calls);
    }

    [Fact]
    public async Task Safe_header_filename_redirects_create_only_guarded_replacement_and_preflight_rejections()
    {
        using var host = new Factory(); using var client = Owner(host); var (owner, session) = await Create(client);
        host.Transport.Responses.Enqueue(new(302, new Uri("https://cdn.example/file"), null, []));
        host.Transport.Responses.Enqueue(new(200, null, null, [0,255,1], "attachment; filename*=UTF-8''..%2F..%2Freport%20file.bin"));
        var result = await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = "https://official.example/file" }); Success(result);
        Assert.Equal("/home/downloads/report_file.bin", result.GetProperty("path").GetString()); Assert.Equal("https://cdn.example/file", result.GetProperty("finalUrl").GetString());
        Assert.Equal("application/octet-stream", result.GetProperty("contentType").GetString());
        foreach (var args in new object[] { new { url = "https://official.example/file", path = "/home/downloads/report_file.bin" },
            new { url = "https://official.example/file", path = "/home/../escape.bin" }, new { url = "https://official.example/file", path = "/agent/file.bin" },
            new { url = "https://official.example/file", path = "/home/downloads/report_file.bin", expectedSha256 = new string('0',64) },
            new { url = "https://official.example/file", path = "/home/a.bin", authorization = "secret" } })
            Assert.True((await Tool(host.Services, owner, session, ToolCatalog.WebDownload, args)).TryGetProperty("error", out _));
        Assert.Equal(2, host.Transport.Calls);
        host.Transport.Responses.Enqueue(new(200, null, "application/octet-stream", [128,0,255]));
        var replaced = await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = "https://official.example/file", path = "/home/downloads/report_file.bin", expectedSha256 = result.GetProperty("sha256").GetString() }); Success(replaced);
        var old = (await host.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 28))!;
        var tools = host.Services.GetRequiredService<SessionToolExecutor>();
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(old, ToolCatalog.WebDownload, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner, OwnedSessionId: session)));
        Assert.Equal(ToolEffect.Write, ToolRegistry.Get(ToolCatalog.WebDownload).Effect); Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.WebDownload));
    }

    [Fact]
    public async Task Normal_definition_authorization_and_on_demand_discovery_offer_download_without_granting_old_versions()
    {
        using var host = new Factory(); using var client = Owner(host); var (owner,session) = await Create(client);
        var definition = (await host.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var gate = host.Services.GetRequiredService<IToolConfigurationGate>();
        var context = new AgentContext(definition,[],"",null,SessionMode.Text,null,false,null,new(Guid.NewGuid(),TriggerKind.UserTurn,"Download a Word template"),
            AgentInstanceId:owner,OwnedSessionId:session,AgentWorkspaceAvailable:true);
        Assert.DoesNotContain(ToolCatalog.For(definition,context,gate),t=>t.Name==ToolCatalog.WebDownload);
        using var args = JsonDocument.Parse("""{"query":"web.download","limit":1}""");
        var loaded = CapabilityDiscoveryMatcher.Load(definition,context,gate,args.RootElement,0);
        Assert.Equal(new[] {ToolCatalog.WebDownload},loaded.Loaded);
        Assert.Contains(ToolCatalog.For(definition,context with {LoadedCapabilityIds=loaded.Loaded},gate),t=>t.Name==ToolCatalog.WebDownload);
        var old = (await host.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant",28))!;
        Assert.Empty(CapabilityDiscoveryMatcher.Load(old,context with {Definition=old},gate,args.RootElement,0).Loaded);
    }

    [Theory]
    [InlineData(".docx", "<html><body>Login</body></html>")]
    [InlineData(".docx", "PK\u0003\u0004corrupt")]
    [InlineData(".pdf", "not a pdf")]
    public async Task Mismatched_or_corrupt_files_never_commit(string extension, string content)
    {
        using var host = new Factory(); using var client = Owner(host); var (owner, session) = await Create(client);
        host.Transport.Responses.Enqueue(new(200, null, "application/octet-stream", System.Text.Encoding.UTF8.GetBytes(content)));
        Assert.Contains("file_type_mismatch", (await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url = "https://official.example/file" + extension, path = "/home/file" + extension })).ToString());
        Assert.Empty((await host.Services.GetRequiredService<IAgentInstanceWorkspaceStore>().ListAsync(owner, "/home", null, 100)).Items);
    }

    [Fact]
    public async Task Cancellation_after_transfer_and_workspace_quota_failure_leave_no_file_or_partial_blob()
    {
        using var host = new Factory(maxFileBytes: 2); using var client = Owner(host); var (owner, session) = await Create(client);
        host.Transport.Responses.Enqueue(new(200, null, null, [0,255,128]));
        var quota = await Tool(host.Services, owner, session, ToolCatalog.WebDownload, new { url="https://official.example/file.bin",path="/home/file.bin" });
        Assert.Contains("WorkspaceQuotaExceeded",quota.ToString());
        using var cancellation = new CancellationTokenSource(); host.Transport.BeforeReturn = cancellation.Cancel;
        host.Transport.Responses.Enqueue(new(200, null, null, [0,255]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(host.Services,owner,session,ToolCatalog.WebDownload,new { url="https://official.example/file.bin",path="/home/cancelled.bin" },cancellation.Token));
        Assert.Empty((await host.Services.GetRequiredService<IAgentInstanceWorkspaceStore>().ListAsync(owner,"/home",null,100)).Items);
        var root = host.Services.GetRequiredService<AgentCore.Infrastructure.Persistence.PersistenceOptions>().WorkspaceRoot;
        var blobRoot = AgentCore.Infrastructure.Workspaces.AgentWorkspacePhysicalPaths.HomeBlobRoot(root,owner);
        Assert.False(Directory.Exists(blobRoot) && Directory.EnumerateFiles(blobRoot).Any());
    }

    [Fact]
    public async Task Download_survives_sqlite_host_restart_and_failed_metadata_commit_cleans_up_new_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(),"download-sqlite-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid owner; Guid session; byte[] bytes = [0,255,128,13,10];
        try
        {
            using (var host = new SqliteFactory(root))
            {
                using var client = Owner(host); (owner,session) = await Create(client);
                host.Transport.Responses.Enqueue(new(200,null,null,bytes));
                Success(await Tool(host.Services,owner,session,ToolCatalog.WebDownload,new {url="https://official.example/file.bin",path="/home/file.bin"}));
                await using (var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
                    await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_download_insert BEFORE INSERT ON AgentWorkspaceItems BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
                host.Transport.Responses.Enqueue(new(200,null,null,bytes));
                var failed = await Tool(host.Services,owner,session,ToolCatalog.WebDownload,new {url="https://official.example/file.bin",path="/home/failed.bin"});
                Assert.True(failed.TryGetProperty("error",out _),failed.ToString());
                Assert.Single(Directory.GetFiles(AgentWorkspacePhysicalPaths.HomeBlobRoot(Path.Combine(root,"workspaces"),owner)));
            }
            using (var reopened = new SqliteFactory(root))
            {
                using var client = Owner(reopened); var (_,next) = await Create(client,owner);
                var file = await client.GetAsync($"/api/v2/sessions/{next}/workspace/content?path=/home/file.bin"); file.EnsureSuccessStatusCode(); Assert.Equal(bytes,await file.Content.ReadAsByteArrayAsync());
                Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/v2/sessions/{next}/workspace/content?path=/home/failed.bin")).StatusCode);
                Assert.Equal(0,reopened.Transport.Calls);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ZipPart(byte[] bytes, string name)
    { using var zip = new ZipArchive(new MemoryStream(bytes)); using var stream = zip.GetEntry(name)!.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
    private static void Success(JsonElement result) => Assert.False(result.TryGetProperty("error", out _), result.ToString());
    private static HttpClient Owner(WebApplicationFactory<Program> host)
    { var client = host.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services)); return client; }
    private static async Task<(Guid Owner, Guid Session)> Create(HttpClient client, Guid? existing = null)
    {
        var owner = existing;
        if (owner is null) { var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 29 }); created.EnsureSuccessStatusCode(); owner = Guid.Parse((await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId); }
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = owner, mode = "text" }); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return (owner.Value, json.RootElement.GetProperty("sessionId").GetGuid());
    }
    private static async Task<JsonElement> Tool(IServiceProvider services, Guid owner, Guid session, string name, object args, CancellationToken ct = default)
    {
        var definition = (await services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var result = await services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, session, new(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), 256 * 1024, cancellationToken: ct,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner, OwnedSessionId: session));
        using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
    }
    private sealed class Factory(long? maxFileBytes = null) : AgentCoreApiFactory
    {
        public FixtureTransport Transport { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => { services.RemoveAll<IPublicWebTransport>(); services.AddSingleton<IPublicWebTransport>(Transport);
                services.RemoveAll<IWebSearchProvider>(); services.AddSingleton<IWebSearchProvider>(new OfficialFixtureSearch());
                if (maxFileBytes.HasValue)
                {
                    services.RemoveAll<IAgentInstanceWorkspaceStore>();
                    services.AddSingleton<IAgentInstanceWorkspaceStore>(sp => new AgentCore.Infrastructure.Workspaces.FileAgentInstanceWorkspaceStore(
                        sp.GetRequiredService<AgentCore.Infrastructure.Persistence.PersistenceOptions>().WorkspaceRoot, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IIdGenerator>(), maxFileBytes: maxFileBytes.Value));
                } });
        }
    }
    private sealed class SqliteFactory(string root) : DurableSqliteHostFactory(Path.Combine(root,"store.db"),runScheduler:false)
    {
        public FixtureTransport Transport { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPublicWebTransport>(); services.AddSingleton<IPublicWebTransport>(Transport);
                services.RemoveAll<ISessionWorkspace>();
                services.AddSingleton<ISessionWorkspace>(sp => new FileSessionWorkspace(Path.Combine(root,"workspaces"),Path.Combine(root,"templates"),sessions:sp.GetRequiredService<IMemoryStore>()));
                services.RemoveAll<IAgentInstanceWorkspaceStore>();
                services.AddSingleton<IAgentInstanceWorkspaceStore>(sp => new FileAgentInstanceWorkspaceStore(Path.Combine(root,"workspaces"),sp.GetRequiredService<TimeProvider>(),sp.GetRequiredService<IIdGenerator>(),sp.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            });
        }
    }
    private sealed class OfficialFixtureSearch : IWebSearchProvider
    {
        public bool IsAvailable => true;
        public ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request,CancellationToken ct=default) =>
            ValueTask.FromResult(new WebSearchResult([new("Official form (controlled offline fixture)","https://official.example/forms/application.docx","Download the original template.")],false));
    }
    private sealed class FixtureTransport : IPublicWebTransport
    {
        public Queue<PublicWebTransportResponse> Responses { get; } = new();
        public int Calls { get; private set; }
        public Action? BeforeReturn { get; set; }
        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("Download must use the bounded transport path.");
        public ValueTask<PublicWebTransportResponse> SendAsync(PublicWebOutboundRequest request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; Assert.Equal("GET", request.Method); Assert.Empty(request.Body); Assert.Equal(10 * 1024 * 1024, request.MaxResponseBytes); BeforeReturn?.Invoke(); return ValueTask.FromResult(Responses.Dequeue()); }
    }
}
