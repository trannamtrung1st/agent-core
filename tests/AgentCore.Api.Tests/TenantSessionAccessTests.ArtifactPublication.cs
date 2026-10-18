using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    public async Task Tenant_model_publication_returns_one_exact_downloadable_file_only_under_current_Allowed_authority(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            const string content = "# Private report\nActual model-to-tool UTF-8 publication: thử nghiệm";
            var model = new ApprovalRecordingModel { ProposedArtifact = JsonSerializer.Serialize(new { displayName = "private-report.md", content }) };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(principal, tenant, null, "Publication in this Session", mode == "missing" ? ["sessions.read"] : ["sessions.read", "artifacts.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(principal, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, session), await Revision(owner, tenant));
            if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/artifacts.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var accepted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish an immutable report", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); var runId = (await Json(accepted)).GetProperty("runId").GetGuid();
            var run = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, false);
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            var result = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ArtifactsCreate);
            using var json = JsonDocument.Parse(result.Text); Assert.True(json.RootElement.TryGetProperty("applied", out var applied), result.Text); Assert.Equal(mode == "allowed", applied.GetBoolean());
            Assert.Equal(mode == "allowed" ? "allowed" : "requiresApproval", json.RootElement.GetProperty("decision").GetString());
            Assert.Contains(model.Requests, r => r.Tools?.Any(t => t.Name == ToolCatalog.ArtifactsCreate && t.ParametersJson.Contains("additionalProperties")) == true);
            var receipt = await factory.Services.GetRequiredService<ITenantArtifactOperationStore>().FindDirectOperationReceiptAsync(tenant, runId, "direct-artifact");
            var items = (await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray().ToArray();
            if (mode == "allowed")
            {
                Assert.NotNull(receipt); Assert.Null(receipt.RequestId); var artifact = Assert.Single(items).GetProperty("artifactId").GetGuid(); Assert.Equal(receipt.OperationId, artifact);
                Assert.Equal(content, await owner.GetStringAsync(api + $"/sessions/{session}/artifacts/{artifact}/content"));
                Assert.Equal(1, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == TenantArtifactOperationRules.AuditAction));
                Assert.Equal(1, (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
            }
            else { Assert.Null(receipt); Assert.Empty(items); }
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.False((await factory.Services.GetRequiredService<IamAuthorizer>().CheckAsync(executor, "artifacts.read", AccessScope.ForTenant(tenant))).Allowed);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
