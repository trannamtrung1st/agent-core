using System.Net;
using System.Net.Http.Json;
using System.Text;
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

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false, false, "approve")] [InlineData(true, false, false, "approve")]
    [InlineData(false, true, false, "approve")] [InlineData(true, true, false, "approve")]
    [InlineData(false, true, true, "approve")] [InlineData(true, true, true, "approve")]
    [InlineData(false, false, false, "reject")] [InlineData(true, false, false, "reject")]
    public Task Workspace_read_Review_model_HTTP_returns_only_the_exact_saved_observation(bool sqlite, bool working, bool list, string decision) => WorkspaceReadReviewFlow(sqlite, working, list, decision);
    [Fact]
    public Task Workspace_read_Review_pending_consent_survives_actual_SQLite_application_host_recreation() => WorkspaceReadReviewFlow(true, false, false, "approve", restart: true);

    [Theory] [InlineData(false, false, "approve")] [InlineData(true, false, "approve")]
    [InlineData(false, true, "approve")] [InlineData(true, true, "approve")]
    [InlineData(false, false, "reject")] [InlineData(true, false, "reject")]
    public Task Tenant_workspace_search_Review_model_HTTP_returns_exact_content_and_name_matches(bool sqlite, bool working, string decision) => WorkspaceReadReviewFlow(sqlite, working, false, decision, search: true);
    [Fact]
    public Task Tenant_workspace_search_pending_consent_survives_actual_SQLite_host_recreation() => WorkspaceReadReviewFlow(true, true, false, "approve", restart: true, search: true);

    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    public Task Tenant_resource_read_model_HTTP_returns_only_exact_reviewed_source(bool sqlite, string decision) => WorkspaceReadReviewFlow(sqlite, false, false, decision, resource: true);
    [Fact]
    public Task Tenant_resource_read_pending_consent_survives_actual_SQLite_host_recreation() => WorkspaceReadReviewFlow(true, false, false, "approve", restart: true, resource: true);

    [Theory] [InlineData(false, false, "approve")] [InlineData(true, false, "approve")]
    [InlineData(false, true, "approve")] [InlineData(true, true, "approve")]
    [InlineData(false, false, "reject")] [InlineData(true, false, "reject")]
    public Task Tenant_resource_namespace_model_HTTP_returns_exact_collection(bool sqlite, bool search, string decision) => WorkspaceReadReviewFlow(sqlite, false, !search, decision, search: search, resource: true);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_resource_namespace_pending_consent_survives_actual_SQLite_host_recreation(bool search) => WorkspaceReadReviewFlow(true, false, !search, "approve", restart: true, search: search, resource: true);

    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public Task Tenant_knowledge_model_HTTP_returns_only_exact_approved_source(bool sqlite, string decision) => WorkspaceReadReviewFlow(sqlite, false, false, decision, resource: true, knowledge: true);
    [Fact]
    public Task Tenant_knowledge_pending_consent_survives_actual_SQLite_host_recreation() => WorkspaceReadReviewFlow(true, false, false, "approve", restart: true, resource: true, knowledge: true);

    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public Task Space_knowledge_read_Review_uses_actual_source_scope_and_exact_observation(bool sqlite, string decision) =>
        WorkspaceReadReviewFlow(sqlite, false, false, decision, resource: true, knowledge: true, spaceResource: true);
    [Fact] public Task Space_knowledge_read_pending_consent_survives_actual_SQLite_host_recreation() =>
        WorkspaceReadReviewFlow(true, false, false, "approve", restart: true, resource: true, knowledge: true, spaceResource: true);

    private async Task WorkspaceReadReviewFlow(bool sqlite, bool working, bool list, string decision, bool restart = false, bool search = false, bool resource = false, bool knowledge = false, bool spaceResource = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-workspace-read-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && !working && !list && !search && decision == "approve" && !resource && System.Environment.GetEnvironmentVariable("AGENTCORE_WORKSPACE_READ_UI_PROOF") == "1";
        const string proofPath = "/private/tmp/agent-core-workspace-read-review-ui";
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            var path = (resource ? "/agent/instance/resources/" : working ? "/working/" : "/home/") + (proof ? "quarterly-workspace-read-review-with-long-name-and-international-試験-thử-nghiệm-2026.txt" : "reviewed-note.txt");
            const string body = "Exact saved workspace body: thử nghiệm. No wider file scope.";
            var model = new WorkspaceRecordingModel(path) { Reviewed = true, ReviewTool = knowledge ? ToolCatalog.KnowledgeRetrieve : search ? ToolCatalog.WorkspaceSearch : list ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead };
            void Configure(IServiceCollection services) { if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"]; }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initial; if (proof) factory.UseKestrel(0);
            using var firstOwner = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            HttpClient owner = firstOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var instanceRevision = 1L; string? knowledgeIdentity = null;
            if (spaceResource)
            {
                var home = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/spaces/{home.SpaceId}/workspace/content?path=/home/reviewed-note.txt&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes(body)))).StatusCode);
                var spaceSourceId=Guid.NewGuid();
                var savedResponse=await owner.PutAsJsonAsync(api+$"/spaces/{home.SpaceId}/resources/{spaceSourceId}",new {
                    sourcePath="/home/reviewed-note.txt",logicalPath="reviewed-note.txt",kind="Knowledge",expectedRevision=await Revision(owner,tenant),expectedSpaceRevision=home.Revision,expectedResourceRevision=0 });
                Assert.True(savedResponse.IsSuccessStatusCode,await savedResponse.Content.ReadAsStringAsync());
                var key=AgentCore.Domain.Spaces.SpaceResourceKeys.Create(home.SpaceId,spaceSourceId);
                path=AgentCore.Domain.Spaces.SpaceResourceKeys.VirtualPath(home.SpaceId,spaceSourceId,"reviewed-note.txt");
                model.KnowledgeIdentity=knowledgeIdentity=key;
                var member=Assert.Single(await factory.Services.GetRequiredService<ITenantSpaceStore>().ReadMembershipsAsync(tenant,home.SpaceId));
                Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/spaces/{home.SpaceId}/memberships/{instance}",new {
                    responsibility="Reader",instructions="",disabledInheritedBindingKeys=Array.Empty<string>(),addedBindings=new[]{new{kind="SpaceFile",resourceKey=key}},
                    expectedRevision=await Revision(owner,tenant),expectedSpaceRevision=home.Revision+1,expectedMembershipRevision=member.Revision })).StatusCode);
            }
            else if (resource)
            {
                var savedResponse = await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    input = new { logicalPath = "reviewed-note.txt", kind = knowledge ? AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge : AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(body) } });
                Assert.True(savedResponse.IsSuccessStatusCode, await savedResponse.Content.ReadAsStringAsync());
                var saved = await Json(savedResponse);
                Assert.Equal(path, saved.GetProperty("resource").GetProperty("virtualPath").GetString());
                path = saved.GetProperty("resource").GetProperty("virtualPath").GetString()!;
                instanceRevision = saved.GetProperty("instanceRevision").GetInt64();
                if (knowledge) model.KnowledgeIdentity = knowledgeIdentity = saved.GetProperty("resource").GetProperty("key").GetString();
                if (search)
                {
                    var named = await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                        input = new { logicalPath = "saved-name.txt", kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = "text/plain", content = "Unrelated literal body"u8.ToArray() } });
                    Assert.True(named.IsSuccessStatusCode, await named.Content.ReadAsStringAsync());
                    instanceRevision = (await Json(named)).GetProperty("instanceRevision").GetInt64();
                }
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            if (!resource) Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes(body)))).StatusCode);
            if (search && !resource)
            {
                var named = path[..path.LastIndexOf('/')] + "/saved-name.txt";
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(named)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Unrelated literal body"u8.ToArray()))).StatusCode);
            }
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact workspace observation", new[] { "agents.read", "sessions.read", resource ? "resources.read" : "workspaces.read" }.Concat(spaceResource ? ["spaces.read"] : []).ToArray(), await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + (resource ? "resources.read" : "workspaces.read"), new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read only the exact reviewed workspace observation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.True(sent.StatusCode == HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync()); var sourceId = (await Json(sent)).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true);
            Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
            if (search)
            {
                Assert.Contains(model.Requests.First().Tools!, t => t.Name == ToolCatalog.WorkspaceSearch && t.ParametersJson.Contains("maxResults"));
                Assert.Contains(model.Requests.First().Tools!, t => t.Name == ToolCatalog.RequestAuthorization && t.Description.Contains("workspace.search"));
            }
            if (restart)
            {
                await factory.DisposeAsync(); model = new WorkspaceRecordingModel(path) { Reviewed = true, KnowledgeIdentity = knowledgeIdentity, ReviewTool = knowledge ? ToolCatalog.KnowledgeRetrieve : search ? ToolCatalog.WorkspaceSearch : list ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead };
                recreated = new NativeTenantSqliteFactory(root, model); recreatedOwner = recreated.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                factory = recreated; owner = recreatedOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>();
                Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, sourceId))!.Status);
            }
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(TenantOperationKind.WorkspaceRead, request.Operation.Kind);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("inspect_workspace", detail.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean(), detail.GetProperty("review").GetRawText()); Assert.DoesNotContain(body, detail.GetRawText());
            if (search)
            {
                Assert.DoesNotContain("saved-name.txt", detail.GetRawText());
                var rows = detail.GetProperty("review").GetProperty("changes").EnumerateArray();
                Assert.Contains(rows, r => r.GetProperty("field").GetString() == "readPage" && r.GetProperty("after").GetString()!.Contains("glob: **/*.txt; up to 10 matches"));
            }
            var conversationResponse = await owner.GetAsync(api + $"/sessions/{session}/approvals");
            Assert.Equal(HttpStatusCode.OK, conversationResponse.StatusCode);
            var conversationRequest = Assert.Single((await Json(conversationResponse)).GetProperty("items").EnumerateArray());
            Assert.Equal("inspect_workspace", conversationRequest.GetProperty("operation").GetString()); Assert.True(conversationRequest.GetProperty("waiting").GetBoolean());
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, instance, session, path, requestId = request.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-decided")) await Task.Delay(100, deadline.Token);
            }
            else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision, actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            var done = await AwaitBrowserRestartRun(runs, tenant, sourceId, false); Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.NotEqual(sourceId, done.AgentRunId); var message = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var output = JsonDocument.Parse(message.Text); var result = output.RootElement;
            Assert.Equal(decision == "approve", result.GetProperty("readAuthorized").GetBoolean()); Assert.False(result.GetProperty("applied").GetBoolean());
            if (decision == "approve")
            {
                var data = result.GetProperty("workspaceObservation"); Assert.True(data.GetProperty("tenantWorkspaceRead").GetBoolean());
                if (search)
                {
                    var matches = data.GetProperty("matches").EnumerateArray().ToArray(); Assert.Equal(2, matches.Length);
                    var match = Assert.Single(matches, m => m.GetProperty("path").GetString() == path);
                    Assert.Contains(matches, m => m.GetProperty("kind").GetString() == "name" && m.GetProperty("path").GetString()!.EndsWith("/saved-name.txt"));
                    Assert.Equal("content", match.GetProperty("kind").GetString()); Assert.Contains("Exact saved workspace body", match.GetProperty("snippet").GetString());
                }
                else if (list) Assert.Equal(path, Assert.Single(data.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
                else Assert.Equal(body, data.GetProperty("content").GetString());
                if (knowledge) { Assert.Equal(knowledgeIdentity, data.GetProperty("identity").GetString()); Assert.Equal(path, data.GetProperty("citation").GetString()); }
                var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
                Assert.Equal(TenantWorkspaceReadOperationRules.Reason, receipt!.ReasonCode); Assert.Equal(done.AgentRunId, receipt.RunId);
            }
            else Assert.Equal(JsonValueKind.Null, result.GetProperty("workspaceObservation").ValueKind);
            var completedConversation = await owner.GetAsync(api + $"/sessions/{session}/approvals"); Assert.Equal(HttpStatusCode.OK, completedConversation.StatusCode);
            Assert.Equal("inspect_workspace", Assert.Single((await Json(completedConversation)).GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{\"completed\":true}");
                var secondSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var secondSent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{secondSession}/messages", new { messageId = Guid.NewGuid(), text = "Review the same exact path under a fresh task", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var secondId = secondSent.GetProperty("runId").GetGuid(); var secondWaiting = await AwaitBrowserRestartRun(runs, tenant, secondId, true);
                Assert.Equal(AgentRunStatus.WaitingForApproval, secondWaiting.Status);
                var staleRequest = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, secondWaiting.ApprovalRequestId!.Value))!;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Changed bytes require a fresh exact review."u8.ToArray()))).StatusCode);
                var staleDetail = await Json(await owner.GetAsync(api + $"/approvals/{staleRequest.RequestId}"));
                Assert.False(staleDetail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                await File.WriteAllTextAsync(proofPath + "-stale.json", JsonSerializer.Serialize(new { tenant, session = secondSession, requestId = staleRequest.RequestId }));
                using var staleDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-stale-cancelled")) await Task.Delay(100, staleDeadline.Token);
                var secondDone = await AwaitBrowserRestartRun(runs, tenant, secondId, false); Assert.Equal(AgentRunStatus.Completed, secondDone.Status);
                var staleResult = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
                Assert.DoesNotContain("Changed bytes", staleResult); Assert.DoesNotContain(body, staleResult);
                Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
                await File.WriteAllTextAsync(proofPath + "-stale-completed.json", "{\"completed\":true}");

                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
