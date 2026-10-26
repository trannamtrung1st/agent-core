using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
using AgentCore.Api.Security;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "docx", "create", "allowed")]
    [InlineData(false, "docx", "create", "review")]
    [InlineData(false, "docx", "create", "grant")]
    [InlineData(false, "docx", "edit", "allowed")]
    [InlineData(false, "docx", "edit", "review")]
    [InlineData(false, "docx", "edit", "grant")]
    [InlineData(false, "xlsx", "create", "allowed")]
    [InlineData(false, "xlsx", "create", "review")]
    [InlineData(false, "xlsx", "create", "grant")]
    [InlineData(false, "xlsx", "edit", "allowed")]
    [InlineData(false, "xlsx", "edit", "review")]
    [InlineData(false, "xlsx", "edit", "grant")]
    [InlineData(true, "docx", "create", "allowed")]
    [InlineData(true, "docx", "create", "review")]
    [InlineData(true, "docx", "create", "grant")]
    [InlineData(true, "docx", "edit", "allowed")]
    [InlineData(true, "docx", "edit", "review")]
    [InlineData(true, "docx", "edit", "grant")]
    [InlineData(true, "xlsx", "create", "allowed")]
    [InlineData(true, "xlsx", "create", "review")]
    [InlineData(true, "xlsx", "create", "grant")]
    [InlineData(true, "xlsx", "edit", "allowed")]
    [InlineData(true, "xlsx", "edit", "review")]
    [InlineData(true, "xlsx", "edit", "grant")]
    [InlineData(false, "docx", "edit", "reject")] [InlineData(true, "xlsx", "edit", "reject")]
    [InlineData(false, "docx", "edit", "stale")] [InlineData(true, "xlsx", "edit", "stale")]
    [InlineData(false, "docx", "edit", "revoked")] [InlineData(true, "xlsx", "edit", "revoked")]
    [InlineData(false, "docx", "edit", "missing-read")] [InlineData(true, "xlsx", "edit", "missing-read")]
    [InlineData(true, "docx", "create", "restart")] [InlineData(true, "xlsx", "edit", "restart")]
    public async Task Tenant_Office_mutations_use_current_authority_exact_consent_and_native_packages(bool sqlite, string format, string action, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-office-mutation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            var proof = !sqlite && format == "docx" && action == "create" && mode == "review" && System.Environment.GetEnvironmentVariable("AGENTCORE_OFFICE_MUTATION_UI_PROOF") == "1";
            const string proofPath = "/private/tmp/agent-core-office-mutation-ui";
            var refused = mode is "reject" or "stale" or "revoked" or "missing-read";
            var reviewed = mode is not ("allowed" or "missing-read"); var working = format == "xlsx"; var edit = action == "edit";
            var path = (working ? "/working/" : "/home/") + (proof ? "exact-reviewed-Office-change-with-long-name-thử-nghiệm-試験-2026." : "exact.") + format;
            var tool = "office." + (working ? "spreadsheet" : "document") + "." + action;
            var model = new TenantOfficeMutationModel(tool, reviewed);
            void Configure(IServiceCollection services) { if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"]; }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initial;
            if (proof) factory.UseKestrel(0);
            using var firstOwner = factory.CreateClient(); HttpClient owner = firstOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            const string text = "Exact new Office content thử nghiệm";
            var processor = factory.Services.GetRequiredService<IOfficeProcessor>();
            var original = working ? processor.CreateWorkbook(new([new("試験", [[new("text", Text: "Private original")]])]), default).Bytes
                : processor.CreateDocument(new(null, [new(null, ["Private original"], null)]), default).Bytes;
            if (edit) Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(original))).StatusCode);
            model.Arguments = !edit ? working ? JsonSerializer.Serialize(new { path, sheets = new[] { new { name = "試験", rows = new[] { new[] { new { type = "text", text } } } } } })
                : JsonSerializer.Serialize(new { path, title = "Exact title", sections = new[] { new { heading = "Exact heading", paragraphs = new[] { text } } } })
                : working ? JsonSerializer.Serialize(new { path, operations = new[] { new { operation = "set_cells", sheet = "試験", range = "A1", cells = new[] { new[] { new { type = "text", text } } } } } })
                : JsonSerializer.Serialize(new { path, operations = new[] { new { operation = "replace_paragraph", selector = processor.InspectDocument(original, 0, 20, default).Elements[0].Selector, text } } });
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var permissions = new List<string> { "agents.read", "sessions.read" }; if ((edit || proof) && mode != "missing-read") permissions.Add("workspaces.read"); if (mode != "grant") permissions.Add("workspaces.write");
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact Office writer", permissions, await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (reviewed) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.write", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Apply this exact Office change", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var id = sent.GetProperty("runId").GetGuid();
            TenantApprovalRequest? request = null;
            if (reviewed)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, id, true); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                if (mode == "restart")
                {
                    await factory.DisposeAsync(); model = new TenantOfficeMutationModel(tool, reviewed) { Arguments = model.Arguments };
                    recreated = new NativeTenantSqliteFactory(root, model); recreatedOwner = recreated.CreateClient(); factory = recreated; owner = recreatedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, id))!.Status);
                }
                request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.Equal(tool, request.Operation.WorkspaceMutation!.ToolName);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                Assert.DoesNotContain("Private original", detail.GetRawText()); Assert.DoesNotContain(request.Operation.WorkspaceMutation.ObservationHash, detail.GetRawText());
                Assert.Contains(text, detail.GetProperty("review").GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "workspaceMutationArguments").GetProperty("after").GetString());
                if (mode == "stale")
                {
                    original = processor.CreateDocument(new(null, [new(null, ["Concurrent source"], null)]), default).Bytes;
                    if (working) original = processor.CreateWorkbook(new([new("試験", [[new("text", Text: "Concurrent source")]])]), default).Bytes;
                    Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(original))).StatusCode);
                }
                if (mode == "revoked") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                if (mode is "stale" or "revoked")
                {
                    detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                }
                else if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenantId = tenant, requestId = request.RequestId, sessionId = session, instanceId = instance }));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-approved")) await Task.Delay(100, deadline.Token);
                }
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false); Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            var bytes = await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path));
            if (refused) Assert.Equal(original, bytes);
            else if (working) Assert.Equal(text, processor.InspectWorkbook(bytes, "試験", "A1", default).Rows[0][0].Value);
            else Assert.Contains(processor.InspectDocument(bytes, 0, 20, default).Elements, e => e.Text == text);
            var result = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == (reviewed ? ToolCatalog.RequestAuthorization : tool)).Text;
            using var output = JsonDocument.Parse(result);
            if (mode == "missing-read") Assert.Equal("executor_read_required", output.RootElement.GetProperty("reasonCode").GetString());
            else Assert.Equal(!refused, output.RootElement.GetProperty("applied").GetBoolean()); Assert.DoesNotContain(text, result); if (mode != "missing-read") Assert.Contains("\"replayAllowed\":false", result);
            Assert.Equal(refused ? 0 : 1, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
            Assert.Empty((await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray());
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{}");
                var nextSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var nextWorkspace = api + $"/sessions/{nextSession}/workspace"; var nextPath = "/working/exact-reviewed-Excel-change-with-long-name-thử-nghiệm-試験-2026.xlsx";
                var sourceBytes = processor.CreateWorkbook(new([new("Quarterly results 試験 thử", [[new("text", Text: "Private original values")]])]), default).Bytes;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(nextWorkspace + $"/content?path={Uri.EscapeDataString(nextPath)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(sourceBytes))).StatusCode);
                model.Select(ToolCatalog.OfficeSpreadsheetEdit, JsonSerializer.Serialize(new { path = nextPath, operations = new[] { new { operation = "set_cells", sheet = "Quarterly results 試験 thử", range = "A1:C1", cells = new[] { new object[] { new { type = "number", number = 0 }, new { type = "boolean", boolean = false }, new { type = "text", text = "<script>literal only</script> thử nghiệm", formula = "1+1", format = "general" } } } } } }));
                var nextSent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{nextSession}/messages", new { messageId = Guid.NewGuid(), text = "Edit only the exact reviewed Office source", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var nextRun = nextSent.GetProperty("runId").GetGuid(); var nextWaiting = await AwaitBrowserRestartRun(runs, tenant, nextRun, true); Assert.Equal(AgentRunStatus.WaitingForApproval, nextWaiting.Status);
                var nextRequest = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, nextWaiting.ApprovalRequestId!.Value))!;
                await File.WriteAllTextAsync(proofPath + "-xlsx.json", JsonSerializer.Serialize(new { requestId = nextRequest.RequestId, sessionId = nextSession }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-xlsx-reviewed")) await Task.Delay(100, deadline.Token);
                sourceBytes = processor.CreateWorkbook(new([new("Quarterly results 試験 thử", [[new("text", Text: "Concurrent current values")]])]), default).Bytes;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(nextWorkspace + $"/content?path={Uri.EscapeDataString(nextPath)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(sourceBytes))).StatusCode);
                await File.WriteAllTextAsync(proofPath + "-stale.json", "{}");
                while (!File.Exists(proofPath + "-cancelled")) await Task.Delay(100, deadline.Token);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, tenant, nextRun, false)).Status);
                Assert.Equal(sourceBytes, await owner.GetByteArrayAsync(nextWorkspace + "/content?path=" + Uri.EscapeDataString(nextPath)));
                using var nextOutput = JsonDocument.Parse(model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text);
                Assert.False(nextOutput.RootElement.GetProperty("applied").GetBoolean());
                await File.WriteAllTextAsync(proofPath + "-finished.json", "{}");
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
            if (request is not null)
            {
                var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
                Assert.Equal(mode == "grant", grant is not null); if (grant is not null) Assert.Equal(OperationGrantStatus.Consumed, grant.Status);
            }
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class TenantOfficeMutationModel(string tool, bool reviewed) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(); public ModelCapabilities Capabilities => inner.Capabilities;
        public string Arguments { get; set; } = "{}";
        public void Select(string nextTool, string args) { tool = nextTool; Arguments = args; }
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); var name = reviewed ? ToolCatalog.RequestAuthorization : tool;
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == name))
            {
                Assert.Contains(request.Tools!, t => t.Name == tool); Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(tool));
                yield return new ModelToolCallEvent(new("exact-office-mutation", name, reviewed ? JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(Arguments) } }) : Arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
}
