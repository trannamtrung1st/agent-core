using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task<AgentRun> NamespaceTerminal(IServiceProvider services, Guid tenant, Guid run)
    {
        try { return await AttachmentTerminal(services.GetRequiredService<ITenantRunStore>(), tenant, run); }
        catch (Xunit.Sdk.XunitException error)
        {
            var diagnostic = services.GetService<DiagnosticLineLoggerProvider>();
            throw new Xunit.Sdk.XunitException(error.Message + "; diagnostics=" + string.Join("\n", diagnostic?.Lines ?? []) + "; errors=" + string.Join("\n", diagnostic?.Errors.Select(e => e.ToString()) ?? []));
        }
    }
    private static async Task<Guid> UploadNamespaceFile(HttpClient owner, AttachmentExecutionCase v, IServiceProvider services, string name, string type, byte[] bytes)
    {
        var saved = (await services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
        using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", name);
        return (await Json(await owner.PostAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, v.Tenant)}&expectedSessionRevision={saved.Snapshot.Revision}", body))).GetProperty("attachmentId").GetGuid();
    }
    private static async Task<Guid> SendNamespaceMessage(HttpClient owner, AttachmentExecutionCase v, IServiceProvider services, IReadOnlyList<Guid>? attachments = null)
    {
        var saved = (await services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
        return (await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the bound file namespace", attachmentIds = attachments,
            expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }))).GetProperty("runId").GetGuid();
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_attachment_namespace_uses_model_loop_and_exact_shared_read_consent(bool sqlite, bool search, bool review)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-namespace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tool = search ? ToolCatalog.WorkspaceSearch : ToolCatalog.WorkspaceList;
            var model = new AttachmentReadModel { ReadTool = tool, ReviewRead = review };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Debug);
            void Configure(IServiceCollection services) { services.AddSingleton(diagnostics); services.AddLogging(logs => logs.AddProvider(diagnostics)); }
            WebApplicationFactory<Program> Create() => sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            await using var factory = Create(); using var owner = factory.CreateClient();
            var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: tool); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [v.Id]))).Status);
            var pending = await UploadNamespaceFile(owner, v, factory.Services, "Private pending name.txt", "text/plain", "Private pending content"u8.ToArray());
            model.Requests.Clear(); model.ReadEnabled = true;
            model.Arguments = search ? """{"path":"/attachments","query":"read body","glob":"*.txt","maxResults":2}""" : """{"path":"/attachments","limit":1}""";
            var api = $"/api/v2/tenants/{v.Tenant}";
            if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/attachments.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sourceId = await SendNamespaceMessage(owner, v, factory.Services); TenantApprovalRequest? request = null;
            if (review)
            {
                request = await AwaitAttachmentReview(factory.Services, v.Tenant, sourceId);
                Assert.Equal("sessions.read", request.Permission); Assert.Equal(v.Session.ToString("D"), request.ResourceId);
                Assert.Null(request.Operation.AttachmentRead); var member = Assert.Single(TenantApprovalRules.AttachmentReadMembers(request.Operation.WorkspaceRead!)); Assert.Equal(v.Id, member.AttachmentId);
                Assert.DoesNotContain("Private read body", request.Operation.CanonicalActionJson);
                var inventory = factory.Services.GetRequiredService<ITenantApprovalStore>();
                Assert.Equal(0, (await inventory.ListApprovalsAsync(v.Tenant, null, null, new(0, 1), attachmentReadVisibility: new([], [v.Session]))).Total);
                Assert.Equal(1, (await inventory.ListApprovalsAsync(v.Tenant, null, null, new(0, 1), workspaceReadVisibility: new([], [], [v.Session], []), attachmentReadVisibility: new([v.Id], [v.Session]))).Total);
                Assert.Equal(0, (await inventory.ListApprovalsAsync(v.Tenant, null, null, new(0, 1), workspaceReadVisibility: new(null, null, []), attachmentReadVisibility: new([v.Id], [v.Session]))).Total);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal("inspect_workspace", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
                Assert.Equal(1, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            var done = review ? await AwaitBrowserRestartRun(runs, v.Tenant, sourceId, false) : await NamespaceTerminal(factory.Services, v.Tenant, sourceId);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.Equal(2, model.Requests.Count);
            var output = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == (review ? ToolCatalog.RequestAuthorization : tool));
            using var doc = JsonDocument.Parse(output.Text); var data = review ? doc.RootElement.GetProperty("workspaceObservation") : doc.RootElement;
            Assert.True(data.GetProperty("tenantWorkspaceRead").GetBoolean()); Assert.True(data.GetProperty("workspaceRead").GetProperty("attachmentCollection").GetBoolean());
            var result = Assert.Single(data.GetProperty(search ? "matches" : "entries").EnumerateArray()); Assert.Equal("/attachments/" + v.Id.ToString("D"), result.GetProperty("path").GetString());
            Assert.Contains(search ? "Private read body" : "read.txt", output.Text); Assert.DoesNotContain(pending.ToString("D"), output.Text); Assert.DoesNotContain("Private pending", output.Text);
            Assert.False(data.GetProperty("truncated").GetBoolean());
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync(api + $"/sessions/{v.Session}/attachments/{v.Id}/content"));
            if (request is not null) Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(v.Tenant, request.RequestId));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false, "parent")] [InlineData(true, "parent")]
    [InlineData(false, "empty")] [InlineData(true, "empty")]
    [InlineData(false, "scoped")] [InlineData(true, "scoped")]
    [InlineData(false, "capability")] [InlineData(true, "capability")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "other-session")] [InlineData(true, "other-session")]
    public async Task Tenant_attachment_namespace_filters_before_bounds_and_refuses_current_parent_capability_policy(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-namespace-boundaries-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { ReadTool = ToolCatalog.WorkspaceList };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Debug);
            void Configure(IServiceCollection services) { services.AddSingleton(diagnostics); services.AddLogging(logs => logs.AddProvider(diagnostics)); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: ToolCatalog.WorkspaceList); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var api = $"/api/v2/tenants/{v.Tenant}";
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [v.Id]))).Status);
            var hidden = await UploadNamespaceFile(owner, v, factory.Services, "Hidden original.txt", "text/plain", "Secret hidden body"u8.ToArray());
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [hidden]))).Status);
            if (boundary is "parent" or "empty" or "scoped")
            {
                var access = factory.Services.GetRequiredService<TenantAccessService>();
                await access.RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId, await Revision(owner, v.Tenant));
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!; var agent = state.SystemIdentities.Single().PrincipalId;
                var role = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Parent or exact child only", [boundary == "parent" ? "attachments.read" : "sessions.read"], await Revision(owner, v.Tenant));
                await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, agent, role.RoleId, AccessScope.ForTenant(v.Tenant), await Revision(owner, v.Tenant));
                if (boundary == "scoped")
                {
                    var child = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Only original", ["attachments.read"], await Revision(owner, v.Tenant));
                    await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, agent, child.RoleId, TenantAttachmentRules.Scope(v.Tenant, v.Id), await Revision(owner, v.Tenant));
                }
            }
            if (boundary == "capability") Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{value.Instance}/settings/capabilities", new {
                expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1, set = new { selected = new[] { ToolCatalog.WorkspaceList } }, clear = Array.Empty<string>() })).StatusCode);
            if (boundary == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/attachments.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (boundary == "other-session") v = v with { Session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new {
                agentInstanceId = value.Instance, expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid() };
            model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = """{"path":"/attachments","limit":1}""";
            var done = await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services));
            Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(2, model.Requests.Count);
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceList);
            using var doc = JsonDocument.Parse(result.Text);
            if (boundary is "parent" or "capability" or "review")
            {
                Assert.Equal("forbidden", doc.RootElement.GetProperty("error").GetString());
                if (boundary == "review") Assert.Equal("attachment_read_review_required", doc.RootElement.GetProperty("reasonCode").GetString());
            }
            else
            {
                var entries = doc.RootElement.GetProperty("entries"); Assert.Equal(boundary == "scoped" ? 1 : 0, entries.GetArrayLength());
                if (boundary == "scoped") Assert.Equal("/attachments/" + value.Execution.Id.ToString("D"), entries[0].GetProperty("path").GetString());
                Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean()); Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nextPath").ValueKind);
            }
            Assert.DoesNotContain("Hidden original", result.Text); Assert.DoesNotContain(hidden.ToString("D"), result.Text); Assert.DoesNotContain("Secret hidden body", result.Text);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(v.Tenant, null, null, new(0, 100))).Items);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_namespace_paging_budget_search_and_retained_results_keep_current_authority(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-namespace-paging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { ReadTool = ToolCatalog.WorkspaceList, HoldAfterRead = true };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Debug);
            void Configure(IServiceCollection services) { services.AddSingleton(diagnostics); services.AddLogging(logs => logs.AddProvider(diagnostics)); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTools: [ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceSearch]); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var api = $"/api/v2/tenants/{v.Tenant}";
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [v.Id]))).Status);
            var second = await UploadNamespaceFile(owner, v, factory.Services, "negative.txt", "text/plain", "No requested phrase"u8.ToArray());
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [second]))).Status);
            var binary = await UploadNamespaceFile(owner, v, factory.Services, "binary-match.bin", "application/octet-stream", [0, 255, 3]);
            Assert.Equal(AgentRunStatus.Completed, (await NamespaceTerminal(factory.Services, v.Tenant, await SendNamespaceMessage(owner, v, factory.Services, [binary]))).Status);
            model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = """{"path":"/attachments","limit":1}""";
            var runId = await SendNamespaceMessage(owner, v, factory.Services); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var active = (await runs.FindTenantRunAsync(v.Tenant, runId))!; var frame = new TenantRuntimeFrame(active.Owner, v.Session, runId, active.Claim!.Generation);
            var reads = factory.Services.GetRequiredService<TenantWorkspaceRuntimeReadService>(); var seen = new HashSet<string>(); string? cursor = null;
            do
            {
                var call = new ModelToolCall("page", ToolCatalog.WorkspaceList, cursor is null ? model.Arguments : JsonSerializer.Serialize(new { path = "/attachments", limit = 1, cursor }));
                using var page = JsonDocument.Parse(await reads.InspectAsync(frame, call, 4096)); var entry = Assert.Single(page.RootElement.GetProperty("entries").EnumerateArray());
                Assert.True(seen.Add(entry.GetProperty("path").GetString()!)); cursor = page.RootElement.GetProperty("nextPath").GetString();
            } while (cursor is not null);
            Assert.Equal(3, seen.Count);
            foreach (var budget in new[] { 0, 1, 2, 128, 1024 })
            {
                var output = await reads.InspectAsync(frame, new("budget", ToolCatalog.WorkspaceList, model.Arguments), budget);
                Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output), 0, budget); if (output.Length > 0) { using var valid = JsonDocument.Parse(output); }
            }
            using var search = JsonDocument.Parse(await reads.InspectAsync(frame, new("search", ToolCatalog.WorkspaceSearch, """{"path":"/attachments","query":"Private read body"}"""), 4096));
            Assert.Equal("/attachments/" + v.Id.ToString("D"), Assert.Single(search.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());
            using var byName = JsonDocument.Parse(await reads.InspectAsync(frame, new("binary-name", ToolCatalog.WorkspaceSearch, """{"path":"/attachments","query":"binary-match"}"""), 4096));
            Assert.Equal("name", Assert.Single(byName.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("kind").GetString());
            using var noMatch = JsonDocument.Parse(await reads.InspectAsync(frame, new("glob", ToolCatalog.WorkspaceSearch, """{"path":"/attachments","query":"Private read body","glob":"*.pdf"}"""), 4096));
            Assert.Empty(noMatch.RootElement.GetProperty("matches").EnumerateArray());
            await reads.RequireStoredAsync((await runs.FindTenantRunAsync(v.Tenant, runId))!);
            await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId, await Revision(owner, v.Tenant));
            var retained = (await runs.FindTenantRunAsync(v.Tenant, runId))!;
            await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(retained).AsTask());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
