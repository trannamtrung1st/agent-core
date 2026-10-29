using AgentCore.Infrastructure.Workspaces;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AgentCore.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "journey")] [InlineData(true, "journey")]
    [InlineData(false, "default")] [InlineData(true, "default")]
    [InlineData(false, "working")] [InlineData(true, "working")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "default-review")] [InlineData(true, "default-review")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "invalid")] [InlineData(true, "invalid")]
    [InlineData(false, "error")] [InlineData(true, "error")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    public async Task Tenant_public_download_model_uses_owned_files_exact_consent_and_factual_receipts(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-public-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var proof = !sqlite && mode == "default-review" && System.Environment.GetEnvironmentVariable("AGENTCORE_DOWNLOAD_UI_PROOF") == "1";
            const string proofPath = "/private/tmp/agent-core-download-ui";
            var reviewed = mode is "review" or "reject" or "default-review" or "stale";
            var path = mode is "default" or "default-review" ? null : mode == "invalid" ? "/home/../escape.docx" : mode == "working" ? "/working/form.docx" : "/home/form.docx";
            var model = new TenantDownloadModel(path, reviewed, mode == "journey");
            var fetcher = new TenantDownloadFetcher { Error = mode == "error", Gate = mode == "revoked" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
            void Configure(IServiceCollection services)
            {
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"];
                services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(fetcher));
                if (mode == "unknown") services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p => new LostWorkspaceAcknowledgement((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(), () => { })));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = mode == "working" ? api + $"/sessions/{session}/workspace" : await PrivateWorkspacePath(owner, tenant, instance);
            var processor = factory.Services.GetRequiredService<IOfficeProcessor>();
            fetcher.Bytes = processor.CreateDocument(new(null, [new(null, ["Applicant: ______", "Preserve this paragraph"], null)]), default).Bytes;
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var permissions = new List<string> { "agents.read", "sessions.read", "workspaces.read", "artifacts.create", "artifacts.read" }; if (mode != "missing") permissions.Add("workspaces.write");
            var role = await access.SaveRoleAsync(human, tenant, null, "Public template workflow", permissions, await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "stale") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path!)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(fetcher.Bytes))).StatusCode);
            if (reviewed) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.write", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Download the public template, fill the original document and return it", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var id = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (mode == "revoked")
            {
                await fetcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                try { await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant)); }
                finally { fetcher.Gate!.TrySetResult(); }
            }
            if (reviewed)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, id, true); Assert.Equal(0, fetcher.Calls);
                var approval = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ToolCatalog.WebDownload, approval.Operation.WorkspaceMutation!.ToolName);
                if (mode == "stale") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path!)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent([1,2,3]))).StatusCode);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{approval.RequestId}"));
                if (mode == "stale") Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenantId = tenant, requestId = approval.RequestId, sessionId = session, instanceId = instance }));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    while (!File.Exists(proofPath + "-approved")) await Task.Delay(100, deadline.Token);
                }
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{approval.RequestId}/" + (mode == "stale" ? "cancel" : "decision"), new { decision = mode == "reject" ? "reject" : "approve", actionHash = approval.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = approval.Revision })).StatusCode);
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false);
            if (mode is "unknown" or "revoked") { Assert.Equal(AgentRunStatus.Failed, done.Status); Assert.Equal("side-effect-indeterminate", done.Failure!.Code); }
            else Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; Tools: {string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text))}");
            Assert.Equal(mode is "reject" or "missing" or "invalid" or "stale" ? 0 : 1, fetcher.Calls);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            var success = mode is "journey" or "default" or "working" or "review" or "default-review";
            var messages = model.Requests.Last().Messages;
            var result = messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == (reviewed ? ToolCatalog.RequestAuthorization : ToolCatalog.WebDownload));
            if (success)
            {
                Assert.NotNull(result); using var json = JsonDocument.Parse(result.Text);
                var fact = json.RootElement.GetProperty("download"); var actualPath = fact.GetProperty("path").GetString()!;
                Assert.True(fact.GetProperty("untrusted").GetBoolean()); Assert.Equal(fetcher.Bytes.Length, fact.GetProperty("byteCount").GetInt64());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fetcher.Bytes)), fact.GetProperty("sha256").GetString());
                Assert.DoesNotContain(Convert.ToBase64String(fetcher.Bytes), result.Text);
                if (path is not null) Assert.Equal(path, actualPath); else Assert.StartsWith("/home/downloads/", actualPath);
                var bytes = await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(actualPath));
                if (mode == "journey")
                {
                    var elements = processor.InspectDocument(bytes, 0, 20, default).Elements;
                    Assert.Contains(elements, e => e.Text == "Applicant: Nguyen An"); Assert.Contains(elements, e => e.Text == "Preserve this paragraph");
                    var artifact = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray()).GetProperty("artifactId").GetGuid();
                    Assert.Equal(bytes, await owner.GetByteArrayAsync(api + $"/sessions/{session}/artifacts/{artifact}/content"));
                }
                else Assert.Equal(fetcher.Bytes, bytes);
                if (sqlite && !reviewed)
                {
                    var receipt = await factory.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, id, "public-download"); Assert.NotNull(receipt?.PublicDownload);
                    await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
                    {
                        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261023160000_RetireEmbeddedRunApproval"));
                        await db.Database.OpenConnectionAsync(); using var foreignKeys = db.Database.GetDbConnection().CreateCommand(); foreignKeys.CommandText = "PRAGMA foreign_keys";
                        Assert.Equal(1L, await foreignKeys.ExecuteScalarAsync());
                        await db.GetService<IMigrator>().MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
                    }
                    await factory.DisposeAsync();
                    await using var reopened = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()); using var again = reopened.CreateClient(); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
                    Assert.Equal(receipt, await reopened.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, id, "public-download"));
                    Assert.Equal(0, await reopened.Services.GetRequiredService<TenantWorkspaceMutationEffectService>().RecoverAsync());
                    Assert.Equal(bytes, await again.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(actualPath)));
                }
            }
            else if (mode == "stale") Assert.Equal(new byte[] { 1,2,3 }, await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path!)));
            else if (mode != "unknown") Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(workspace + "/content?path=" + Uri.EscapeDataString("/home/form.docx"))).StatusCode);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{}");
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                while (!File.Exists(proofPath + "-done")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class TenantDownloadFetcher : IPublicWebFetcher
    {
        public byte[] Bytes { get; set; } = []; public bool Error { get; init; } public int Calls;
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsDownloads => true;
        public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only exact binary download is expected.");
        public async ValueTask<PublicWebDownloadResult> DownloadAsync(PublicWebDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            return Error ? new(request.Url.AbsoluteUri, null, null, [], "not_found", "Controlled source refusal") : new("https://official.example/final.docx", OfficeLimits.DocumentMime, "form.docx", Bytes, null, null);
        }
    }
    private sealed class TenantDownloadModel(string? path, bool reviewed, bool journey) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(); public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); var tools = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var name = reviewed ? ToolCatalog.RequestAuthorization : ToolCatalog.WebDownload; ModelToolCall? call = null;
            if (!tools.Any(m => m.Name == name))
            {
                var args = path is null ? JsonSerializer.Serialize(new { url = "https://official.example/form.docx" }) : JsonSerializer.Serialize(new { url = "https://official.example/form.docx", path });
                call = new("public-download", name, reviewed ? JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool = ToolCatalog.WebDownload, arguments = JsonSerializer.Deserialize<JsonElement>(args) } }) : args);
            }
            else if (journey)
            {
                var inspected = tools.LastOrDefault(m => m.Name == ToolCatalog.OfficeDocumentInspect);
                if (inspected is null) call = new("inspect-public", ToolCatalog.OfficeDocumentInspect, JsonSerializer.Serialize(new { path }));
                else if (!tools.Any(m => m.Name == ToolCatalog.OfficeDocumentEdit))
                {
                    using var doc = JsonDocument.Parse(inspected.Text); var selector = doc.RootElement.GetProperty("inspection").GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("text").GetString() == "Applicant: ______").GetProperty("selector").GetString();
                    call = new("edit-public", ToolCatalog.OfficeDocumentEdit, JsonSerializer.Serialize(new { path, operations = new[] { new { operation = "replace_paragraph", selector, text = "Applicant: Nguyen An" } } }));
                }
                else if (!tools.Any(m => m.Name == ToolCatalog.ArtifactsCreateFromWorkspace)) call = new("publish-public", ToolCatalog.ArtifactsCreateFromWorkspace, JsonSerializer.Serialize(new { path, displayName = "completed.docx" }));
            }
            if (call is not null) { Assert.Contains(request.Tools!, t => t.Name == call.Name); yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
}
