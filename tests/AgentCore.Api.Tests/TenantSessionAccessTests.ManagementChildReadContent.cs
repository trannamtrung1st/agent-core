using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ChildContentReadHook(IDefinitionResourceContentStore inner) : IDefinitionResourceContentStore, ITenantResourceSourceContentStore
    {
        public string? Hash; public string Mode = ""; public int Reads;
        public ValueTask StoreVerifiedAsync(string hash, byte[] bytes, CancellationToken ct = default) => inner.StoreVerifiedAsync(hash, bytes, ct);
        public ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string hash, long size, CancellationToken ct = default) =>
            ((ITenantResourceSourceContentStore)inner).ReadVerifiedResourceSourceAsync(hash, size, ct);
        public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken ct = default)
        {
            var bytes = await inner.ReadAsync(hash, ct);
            if (Hash != hash) return bytes;
            Interlocked.Increment(ref Reads);
            return Mode == "missing" ? null : Mode == "corrupt" ? new byte[] { 7, 8, 9 } : bytes;
        }
    }
    public static IEnumerable<object[]> ManagementChildContentCases =>
        from sqlite in new[] { false, true }
        from mode in new[] { "procedure", "excerpt", "corrupt", "missing" }
        select new object[] { sqlite, mode };

    [Theory] [MemberData(nameof(ManagementChildContentCases))]
    public Task Canonical_management_child_content_preserves_exact_procedures_marks_excerpts_and_withholds_invalid_bytes(bool sqlite, string mode) => AssertManagementChildContent(sqlite, mode);

    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Canonical_management_child_binary_Review_labels_absent_text_and_never_fetches_or_returns_bytes(bool sqlite) => AssertManagementChildContent(sqlite, "binary");

    private async Task AssertManagementChildContent(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-child-content-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tool = mode == "procedure" ? "skills.inspect" : "resources.inspect"; var model = new ManagementReadModel(tool);
            ChildContentReadHook? hook = null;
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                var original = s.Last(d => d.ServiceType == typeof(IDefinitionResourceContentStore));
                s.Replace(ServiceDescriptor.Singleton<IDefinitionResourceContentStore>(p => hook = new((IDefinitionResourceContentStore)(original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(p) ?? ActivatorUtilities.CreateInstance(p, original.ImplementationType!)))));
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementInspection(host, owner); var api = $"/api/v2/tenants/{tenant}"; long instanceRevision = 6;
            var procedure = new string('p', 1100) + "<script>literal procedure</script>";
            if (mode == "procedure")
            {
                var saved = await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "update", key = "instance:metadata-private", expectedSkillRevision = 1,
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6,
                    input = new { name = "Exact long procedure", description = "An exact complete read", procedure, projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } });
                Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync()); instanceRevision = 7;
                model.ArgumentsOverride = "{\"key\":\"instance:metadata-private\"}";
            }
            else if (mode is "excerpt" or "binary")
            {
                var bytes = mode == "binary" ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=") : System.Text.Encoding.UTF8.GetBytes(new string('λ', 3000));
                var saved = await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6,
                    input = new { logicalPath = mode == "binary" ? "private/bounded-content.png" : "private/bounded-content.txt", kind = mode == "binary" ? 4 : 2, mediaType = mode == "binary" ? "image/png" : "text/plain", content = bytes } });
                Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
                var resource = (await Json(saved)).GetProperty("resource");
                hook!.Hash = resource.GetProperty("contentSha256").GetString(); hook.Mode = mode; instanceRevision = 7;
                model.ArgumentsOverride = JsonSerializer.Serialize(new { key = resource.GetProperty("key").GetString() });
            }
            else
            {
                model.ArgumentsOverride = await ChildReadArguments(owner, tenant, instance, tool);
                var catalog = (await Json(await owner.GetAsync(api + $"/agents/{instance}/resources"))).GetProperty("catalog");
                hook!.Hash = catalog.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("logicalPath").GetString() == "private/metadata.txt").GetProperty("contentSha256").GetString(); hook.Mode = mode;
            }
            if (mode == "binary")
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/resources.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read the exact private content under its own source authority.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            var runId = (await Json(submitted)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>();
            var final = await AwaitManagementRun(runs, tenant, runId, mode == "binary");
            if (mode == "binary")
            {
                Assert.Equal(AgentRunStatus.WaitingForApproval, final.Status);
                var request = (await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, final.ApprovalRequestId!.Value))!;
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Contains("Not provided", detail.GetRawText());
                Assert.DoesNotContain("Inherit Agent default", detail.GetRawText());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                final = await AwaitManagementSuccessor(runs, tenant, session, request.RequestId, runId);
            }
            if (mode == "corrupt")
            {
                // Integrity failure aborts the Runtime before another model invocation,
                // rather than returning corrupted bytes as a recoverable observation.
                Assert.Equal(AgentRunStatus.Failed, final.Status); Assert.Equal(2, model.Requests.Count);
                Assert.Empty(AssertManagementPreludeAndGetInspections(final)); Assert.True(hook!.Reads > 0);
                Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("\"tenantManagementRead\":true", StringComparison.Ordinal));
            }
            else if (mode == "missing")
            {
                var message = model.Requests.SelectMany(r => r.Messages).Last(m => m.Name == tool);
                Assert.DoesNotContain("\"tenantManagementRead\":true", message.Text); Assert.DoesNotContain("PRIVATE-INSPECTION", message.Text);
                Assert.Empty(AssertManagementPreludeAndGetInspections(final)); Assert.True(hook!.Reads > 0);
            }
            else
            {
                var message = model.Requests.SelectMany(r => r.Messages).Last(m => m.Name == (mode == "binary" ? AgentCore.Application.Tools.ToolCatalog.RequestAuthorization : tool));
                Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure}");
                using var result = JsonDocument.Parse(message.Text);
                var observation = mode == "binary" ? result.RootElement.GetProperty("managementReadObservation") : result.RootElement;
                var data = observation.GetProperty("observation").GetProperty("data");
                if (mode == "procedure") Assert.Equal(procedure, data.GetProperty("skill").GetProperty("procedure").GetString());
                else if (mode == "excerpt") { Assert.Equal(new string('λ', 1024), data.GetProperty("content").GetString()); Assert.True(data.GetProperty("truncated").GetBoolean()); }
                else { Assert.Equal(JsonValueKind.Null, data.GetProperty("content").ValueKind); Assert.False(data.GetProperty("contentProvided").GetBoolean()); Assert.Equal(0, hook!.Reads); }
                Assert.Equal(tool, Assert.Single(AssertManagementPreludeAndGetInspections(final)).ToolName);
            }
            Assert.DoesNotContain("instance:metadata-private", final.ActiveSkillKeys);
            Assert.Equal(instanceRevision, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
