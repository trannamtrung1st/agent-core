using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false, "allowed")] [InlineData(true, true, "allowed")]
    [InlineData(false, false, "review")] [InlineData(true, true, "review")]
    [InlineData(false, true, "source-review")] [InlineData(true, false, "source-review")]
    [InlineData(false, true, "grant")] [InlineData(true, false, "grant")]
    [InlineData(false, true, "missing-read")] [InlineData(true, false, "missing-read")]
    [InlineData(false, false, "direct-review")] [InlineData(true, true, "direct-review")]
    [InlineData(false, false, "stale")] [InlineData(true, true, "stale")]
    [InlineData(false, false, "revoked")] [InlineData(true, true, "revoked")]
    [InlineData(false, true, "reject")] [InlineData(true, false, "reject")]
    [InlineData(true, false, "restart")]
    public async Task Tenant_model_workspace_Artifact_preserves_exact_packages_and_current_source_consent(bool sqlite, bool working, string mode)
        => await WorkspaceArtifactFlow(sqlite, working, mode);

    [Theory]
    [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "source-review")] [InlineData(true, "source-review")]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    [InlineData(false, "missing-read")] [InlineData(true, "missing-read")]
    [InlineData(false, "direct-review")] [InlineData(true, "direct-review")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(true, "restart")]
    public Task Tenant_model_resource_Artifact_preserves_exact_package_and_consent(bool sqlite, string mode) => WorkspaceArtifactFlow(sqlite, false, mode, resource: true);

    private async Task WorkspaceArtifactFlow(bool sqlite, bool working, string mode, bool resource = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-source-artifact-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? fresh = null;
        var proof = !resource && !sqlite && !working && mode is "review" or "stale" && Environment.GetEnvironmentVariable("AGENTCORE_SOURCE_ARTIFACT_UI_PROOF") == "1";
        const string proofPath = "/private/tmp/agent-core-source-artifact-ui";
        try
        {
            var reviewed = mode is not ("allowed" or "missing-read" or "direct-review"); var refused = mode is "stale" or "revoked" or "reject" or "missing-read" or "direct-review";
            var model = new ApprovalRecordingModel { ArtifactPublicationTool = ToolCatalog.ArtifactsCreateFromWorkspace, EmitArtifactReference = true };
            void Configure(IServiceCollection services)
            {
                if (!proof) return;
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ArtifactProofClock()));
                ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"];
            }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initial; if (proof) factory.UseKestrel(0);
            using var first = factory.CreateClient(); HttpClient owner = first; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var processor = factory.Services.GetRequiredService<IOfficeProcessor>();
            var bytes = processor.CreateDocument(new("Private source", [new(null, ["Private source body: thử nghiệm"], null)]), default).Bytes;
            string? resourceKey = null; long instanceRevision = 1;
            const string name = "exact-reviewed-source-with-long-name-thử-nghiệm-試験-2026.docx";
            if (resource)
            {
                var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    input = new { logicalPath = name, kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = OfficeLimits.DocumentMime, content = bytes } }));
                resourceKey = saved.GetProperty("resource").GetProperty("key").GetString(); instanceRevision = saved.GetProperty("instanceRevision").GetInt64();
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            var path = (resource ? "/agent/instance/resources/" : working ? "/working/" : "/home/") + name;
            if (!resource) Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
            var args = new { path, displayName = "exact-reviewed-source-thử-nghiệm-試験.docx" };
            if (reviewed) model.ProposedArtifactAuthorization = JsonSerializer.Serialize(new { operation = "publish_workspace_artifact", artifact = args }); else model.ProposedArtifact = JsonSerializer.Serialize(args);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var permissions = new List<string> { "agents.read", "sessions.read", "artifacts.read" }; if (mode != "missing-read") permissions.Add(resource ? "resources.read" : "workspaces.read"); if (mode != "grant") permissions.Add("artifacts.create");
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact source publication", permissions, await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (reviewed && mode != "grant" || mode == "direct-review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + (mode is "source-review" or "direct-review" ? resource ? "resources.read" : "workspaces.read" : "artifacts.create"), new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish the exact saved workspace source", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var id = sent.GetProperty("runId").GetGuid(); TenantApprovalRequest? request = null;
            if (reviewed)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, id, true); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
                if (mode == "restart")
                {
                    await initial.DisposeAsync(); recreated = new NativeTenantSqliteFactory(root, model); fresh = recreated.CreateClient(); owner = fresh; factory = recreated;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, id))!.Status);
                }
                request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); var changes = detail.GetProperty("review").GetProperty("changes");
                Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.Equal(6, changes.GetArrayLength());
                Assert.DoesNotContain("Private source body", detail.GetRawText()); Assert.DoesNotContain(request.Operation.ArtifactPublication!.Source!.ObservationHash, detail.GetRawText());
                Assert.DoesNotContain(changes.EnumerateArray(), c => c.GetProperty("field").GetString() == "artifactContent");
                Assert.Equal(path, changes.EnumerateArray().Single(c => c.GetProperty("field").GetString() == "artifactSourcePath").GetProperty("after").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), changes.EnumerateArray().Single(c => c.GetProperty("field").GetString() == "artifactChecksum").GetProperty("after").GetString());
                if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, session, instance, requestId = request.RequestId, mode }));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-reviewed")) await Task.Delay(100, timeout.Token);
                }
                if (mode == "stale")
                {
                    bytes = processor.CreateDocument(new(null, [new(null, ["Concurrent source"], null)]), default).Bytes;
                    if (resource) Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                        input = new { resourceId = Guid.Parse(resourceKey![9..]), expectedResourceRevision = 1, logicalPath = name, kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = OfficeLimits.DocumentMime, content = bytes } })).StatusCode);
                    else Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
                }
                if (mode == "revoked") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                if (mode is "stale" or "revoked") Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + "-ready.json", "{}"); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while ((await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, request.RequestId, timeout.Token))!.Status == TenantApprovalStatus.Pending) await Task.Delay(100, timeout.Token);
                }
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (mode is "stale" or "revoked" ? "cancel" : "decision"), new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false); Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            var result = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == (reviewed ? ToolCatalog.RequestAuthorization : ToolCatalog.ArtifactsCreateFromWorkspace)).Text;
            using var output = JsonDocument.Parse(result);
            if (mode == "missing-read") Assert.Equal("executor_read_required", output.RootElement.GetProperty("reasonCode").GetString()); else { Assert.True(output.RootElement.TryGetProperty("applied", out var applied), result); Assert.Equal(!refused, applied.GetBoolean()); }
            if (mode == "direct-review") Assert.Equal("requiresApproval", output.RootElement.GetProperty("decision").GetString());
            Assert.DoesNotContain(path, result); Assert.DoesNotContain("Private source", result);
            var items = (await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray().ToArray();
            if (refused) Assert.Empty(items);
            else
            {
                var artifact = Assert.Single(items).GetProperty("artifactId").GetGuid(); Assert.Equal(bytes, await owner.GetByteArrayAsync(api + $"/sessions/{session}/artifacts/{artifact}/content"));
                Assert.Equal(bytes, await owner.GetByteArrayAsync(resource ? api + $"/agents/{instance}/resources/{Uri.EscapeDataString(resourceKey!)}/content" : workspace + "/content?path=" + Uri.EscapeDataString(path)));
                Assert.Equal(mode != "grant", (await factory.Services.GetRequiredService<IamAuthorizer>().CheckAsync(executor, "artifacts.create", TenantSessionRules.Scope(tenant, session))).Allowed);
            }
            var offered = Assert.Single(model.Requests.First().Tools!, t => t.Name == ToolCatalog.ArtifactsCreateFromWorkspace); Assert.Equal(TenantWorkspaceArtifactContract.ForModel(), offered);
            Assert.DoesNotContain(model.Requests.First().Tools!, t => t.Name == ToolCatalog.ArtifactsCreate);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof) { await File.WriteAllTextAsync(proofPath + "-completed.json", "{}"); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30)); while (!File.Exists(proofPath + "-done")) await Task.Delay(100, timeout.Token); }
        }
        finally { fresh?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
