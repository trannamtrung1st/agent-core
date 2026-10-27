using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false, "allowed")] [InlineData(true, false, "allowed")]
    [InlineData(false, true, "allowed")] [InlineData(true, true, "allowed")]
    [InlineData(false, false, "missing")] [InlineData(true, false, "missing")]
    [InlineData(false, true, "review")] [InlineData(true, true, "review")]
    [InlineData(false, false, "home-disabled")] [InlineData(true, false, "home-disabled")]
    public async Task Tenant_workspace_model_HTTP_journey_reads_human_uploaded_bytes_only_under_current_Allowed_policy(bool sqlite, bool working, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-workspace-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = working ? "/working/model-note.txt" : "/home/model-note.txt";
            var model = new WorkspaceRecordingModel(path) { FirstRequestGate = mode == "home-disabled" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            const string body = "Actual human upload → owned model read: thử nghiệm";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes(body)))).StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current workspace model reads", mode == "missing" ? ["agents.read", "sessions.read"] : ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "List then read my workspace note", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            if (mode == "home-disabled")
            {
                await model.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var admitted = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, (await Json(sent)).GetProperty("runId").GetGuid()))!;
                Assert.Equal(AgentCore.Domain.Definitions.HomeAccess.ReadWrite, (admitted.Admission.Configuration!.Definition.StateIsolationPolicy ?? AgentCore.Domain.Definitions.StateIsolationPolicy.Default).HomeAccess);
                try
                {
                    Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new {
                        expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() })).StatusCode);
                }
                finally { model.FirstRequestGate!.TrySetResult(); }
            }
            var run = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, (await Json(sent)).GetProperty("runId").GetGuid(), false);
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            var final = model.Requests.Last();
            foreach (var tool in new[] { ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceRead })
            {
                var message = final.Messages.Last(m => m.Role == ModelRole.Tool && m.Name == tool);
                using var result = JsonDocument.Parse(message.Text);
                if (mode == "allowed")
                {
                    Assert.True(result.RootElement.GetProperty("tenantWorkspaceRead").GetBoolean());
                    if (tool == ToolCatalog.WorkspaceRead) Assert.Equal(body, result.RootElement.GetProperty("content").GetString());
                    else Assert.Equal(path, Assert.Single(result.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
                }
                else { Assert.Equal(mode == "missing" ? "executor_read_required" : mode == "home-disabled" ? "home_access_disabled" : "workspace_read_review_required", result.RootElement.GetProperty("reasonCode").GetString()); Assert.DoesNotContain(body, message.Text); }
                Assert.Contains(model.Requests.First().Tools!, t => t.Name == tool && t.ParametersJson.Contains("additionalProperties"));
            }
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class WorkspaceRecordingModel(string path) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public bool Reviewed { get; init; }
        public string? KnowledgeIdentity { get; set; }
        public string ReviewTool { get; init; } = ToolCatalog.WorkspaceRead;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource? FirstRequestGate { get; init; }
        public TaskCompletionSource FirstRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Requests.Count == 1 && FirstRequestGate is { } gate)
            { FirstRequestEntered.TrySetResult(); await gate.Task.WaitAsync(cancellationToken); }
            if (Reviewed)
            {
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization)
                    && request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("inspect_workspace")) == true)
                {
                    var readArgs = ReviewTool == ToolCatalog.KnowledgeRetrieve ? JsonSerializer.Serialize(new { identity = KnowledgeIdentity }) : ReviewTool == ToolCatalog.WorkspaceSearch ? JsonSerializer.Serialize(new { path = path[..path.LastIndexOf('/')], query = "saved", glob = "**/*.txt", maxResults = 10 })
                        : JsonSerializer.Serialize(new { path = ReviewTool == ToolCatalog.WorkspaceList ? path[..path.LastIndexOf('/')] : path });
                    yield return new ModelToolCallEvent(new("review-workspace", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new {
                        operation = "inspect_workspace", read = new { tool = ReviewTool, arguments = JsonSerializer.Deserialize<JsonElement>(readArgs) } })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
                yield break;
            }
            var tool = !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceList) ? ToolCatalog.WorkspaceList
                : !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead) ? ToolCatalog.WorkspaceRead : null;
            if (tool is not null && request.Tools?.Any(t => t.Name == tool) == true)
            {
                yield return new ModelToolCallEvent(new("owned-" + tool, tool, JsonSerializer.Serialize(new { path = tool == ToolCatalog.WorkspaceList ? path[..path.LastIndexOf('/')] : path })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
}
