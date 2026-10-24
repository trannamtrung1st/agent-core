using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ResourceSourceModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public string Key = "", SourceTool = ToolCatalog.WorkspaceRead, SourceArguments = "{}", Arguments = "{}", OriginalPath = "";
        public bool SourceReview; public string SourceIntent = "inspect_workspace";
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Enqueue(request); var tools = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            ModelToolCall? Call(string name, string args, string id) => request.Tools!.Any(t => t.Name == name) ? new(id, name, args)
                : tools.Any(m => m.ToolCallId == "load-" + id) ? null : new("load-" + id, ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = name, limit = 1 }));
            ModelToolCall? next = null;
            if (!tools.Any(m => m.ToolCallId == "inspect-resource")) next = Call("resources.inspect", JsonSerializer.Serialize(new { key = Key }), "inspect-resource");
            else if (!tools.Any(m => m.ToolCallId == "resource-source"))
                next = SourceReview ? new("resource-source", ToolCatalog.RequestAuthorization, SourceIntent == "run_browser_command"
                    ? JsonSerializer.Serialize(new { operation = SourceIntent, command = new { tool = SourceTool, arguments = JsonSerializer.Deserialize<JsonElement>(SourceArguments) } })
                    : SourceIntent == "read_attachment"
                    ? JsonSerializer.Serialize(new { operation = SourceIntent, read = JsonSerializer.Deserialize<JsonElement>(SourceArguments) })
                    : JsonSerializer.Serialize(new { operation = SourceIntent, read = new { tool = SourceTool, arguments = JsonSerializer.Deserialize<JsonElement>(SourceArguments) } }))
                    : Call(SourceTool, SourceArguments, "resource-source");
            else if (!tools.Any(m => m.ToolCallId == "write-resource")) next = Call("resources.upsert", Arguments, "write-resource");
            else if (!tools.Any(m => m.ToolCallId == "resource-consent"))
            {
                using var doc = JsonDocument.Parse(tools.Last(m => m.ToolCallId == "write-resource").Text);
                if (doc.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
                    next = new("resource-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_management", change = new { tool = "resources.upsert", arguments = JsonSerializer.Deserialize<JsonElement>(Arguments) } }));
            }
            else if (!tools.Any(m => m.ToolCallId == "read-original-pin")) next = Call(ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = OriginalPath }), "read-original-pin");
            if (next is not null) { yield return new ModelToolCallEvent(next); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            yield return new ModelTextDelta("The reviewed literal draft applies to the next Run; the original bytes remain pinned here.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [TenantBrowserChromiumTheory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Resource_authoring_Browser_observation_preserves_independent_read_consent(bool sqlite, bool review) =>
        Resource_authoring_derives_only_from_actual_owned_observations_and_reads_original_pins_after_effect(sqlite, "browser", review);
    public static IEnumerable<object[]> ResourceAttachmentSourceCases => from sqlite in new[] { false, true }
        from virtualRead in new[] { false, true } from review in new[] { false, true }
        select new object[] { sqlite, virtualRead, review };
    [Theory(Timeout = 120000)] [MemberData(nameof(ResourceAttachmentSourceCases))]
    public Task Resource_authoring_attachment_sources_preserve_contextual_projection(bool sqlite, bool virtualRead, bool review) =>
        Resource_authoring_derives_only_from_actual_owned_observations_and_reads_original_pins_after_effect(sqlite, virtualRead ? "attachment_virtual" : "attachment", review);
    public static IEnumerable<object[]> ResourceSourceCases => from sqlite in new[] { false, true }
        from source in new[] { "working", "home", "resource", "knowledge", "office", "attachment", "attachment_virtual" }
        select new object[] { sqlite, source, false };
    [Theory(Timeout = 120000)] [MemberData(nameof(ResourceSourceCases))]
    [InlineData(false, "working", true)] [InlineData(true, "working", true)]
    [InlineData(false, "attachment", true)] [InlineData(true, "attachment", true)]
    [InlineData(false, "attachment_virtual", true)] [InlineData(true, "attachment_virtual", true)]
    public async Task Resource_authoring_derives_only_from_actual_owned_observations_and_reads_original_pins_after_effect(bool sqlite, string source, bool review)
    {
        var root = Path.Combine(Path.GetTempPath(), "resource-source-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ResourceSourceModel { SourceReview = review };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
            void Configure(IServiceCollection services)
            {
                services.AddLogging(logging => logging.AddProvider(diagnostics));
                if (source != "browser") return;
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false, attachmentRead: source.StartsWith("attachment", StringComparison.Ordinal)); var api = $"/api/v2/tenants/{tenant}"; var agentPath = api + $"/agents/{instance}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(agentPath + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Resources", "Settings" } }, autoApply = new { value = Array.Empty<string>() } } })).StatusCode);
            var uploaded = await Json(await owner.PostAsJsonAsync(agentPath + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3,
                input = new { logicalPath = "source.txt", kind = source == "knowledge" ? 1 : 2, mediaType = "text/plain", content = Encoding.UTF8.GetBytes("ORIGINAL-PINNED-RESOURCE-TEXT"), enabled = true } }));
            model.Key = uploaded.GetProperty("resource").GetProperty("key").GetString()!; model.OriginalPath = uploaded.GetProperty("resource").GetProperty("virtualPath").GetString()!;
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 }))).GetProperty("sessionId").GetGuid();
            var path = source == "home" ? "/home/source.txt" : "/working/source.txt"; string sourceName = "workspace:" + path; Guid? attachment = null;
            byte[] bytes = Encoding.UTF8.GetBytes("ACTUAL-OBSERVED-SOURCE-TEXT");
            if (source == "office") { path = "/working/source.docx"; bytes = host.Services.GetRequiredService<IOfficeProcessor>().CreateDocument(new("Source", [new("Section", ["ACTUAL-OFFICE-SOURCE-TEXT"], null)]), default).Bytes; sourceName = "workspace:" + path; model.SourceTool = ToolCatalog.OfficeDocumentInspect; }
            if (source == "browser")
            {
                var native = Assert.IsType<NativePlaywrightBrowser>(host.Services.GetRequiredService<IBrowser>());
                var browserAgent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var browserOwner = new TenantBrowserOwner(tenant, instance, browserAgent.PrincipalId, session);
                Assert.Null(await native.EnsureTenantIsolationAsync(browserOwner, BrowserProfileMode.EphemeralSession));
                sourceName = native.Fixture.Origin + "/browser-native.html";
                var command = new BrowserNavigate(sourceName); var prepared = await native.PrepareTenantCommandAsync(browserOwner, command);
                Assert.Null((await native.ExecuteAsync(new(session, command) { TenantOwner = browserOwner, TenantWitness = prepared.Witness })).ErrorCode);
                model.SourceTool = "browser.snapshot"; model.SourceArguments = "{}"; model.SourceIntent = "run_browser_command";
            }
            else if (source.StartsWith("attachment", StringComparison.Ordinal))
            {
                using var form = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); form.Add(file, "file", "source.txt");
                attachment = (await Json(await owner.PostAsync(api + $"/sessions/{session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", form))).GetProperty("attachmentId").GetGuid();
                model.SourceTool = source == "attachment" ? ToolCatalog.AttachmentsRead : ToolCatalog.WorkspaceRead; model.SourceIntent = source == "attachment" ? "read_attachment" : "inspect_workspace";
                model.SourceArguments = source == "attachment" ? JsonSerializer.Serialize(new { attachmentId = attachment }) : JsonSerializer.Serialize(new { path = "/attachments/" + attachment.Value.ToString("D") });
                sourceName = "attachment:" + attachment.Value.ToString("D");
            }
            else if (source is "resource" or "knowledge")
            {
                model.SourceTool = source == "knowledge" ? ToolCatalog.KnowledgeRetrieve : ToolCatalog.WorkspaceRead;
                model.SourceArguments = source == "knowledge" ? JsonSerializer.Serialize(new { identity = model.Key }) : JsonSerializer.Serialize(new { path = model.OriginalPath });
                sourceName = source == "knowledge" ? "knowledge:" + model.Key : "workspace:" + model.OriginalPath;
            }
            else
            {
                var workspace = api + (source == "home" ? $"/agents/{instance}/workspace" : $"/sessions/{session}/workspace");
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
                model.SourceArguments = JsonSerializer.Serialize(new { path });
            }
            model.Arguments = JsonSerializer.Serialize(new { key = model.Key, expectedResourceRevision = 1, expectedInstanceRevision = 4, logicalPath = "source.txt", kind = source == "knowledge" ? "Knowledge" : "Reference", mediaType = "text/plain", content = "DERIVED-LITERAL-RESOURCE-DRAFT", source = sourceName });
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single().PrincipalId;
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!; var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Resource author with actual source reads", ["agents.read", "resources.read", "resources.manage", "sessions.read", "workspaces.read", "attachments.read", "browser.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + (source == "browser" ? "browser.read" : attachment is null ? "workspaces.read" : "attachments.read"), new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var submit = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read the actual selected source and save a derived literal draft after exact consent.", attachmentIds = attachment is null ? Array.Empty<Guid>() : new[] { attachment.Value }, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode); var initial = (await Json(submit)).GetProperty("runId").GetGuid(); var runId = initial;
            string Trace() => JsonSerializer.Serialize(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.ToolCallId, m.Text }));
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); TenantApprovalRequest? mutation = null; AgentRun done = null!;
            for (var index = 0; index < (review ? 2 : 1); index++)
            {
                var pending = await AwaitManagementRun(runs, tenant, runId, true); Assert.True(pending.Status == AgentRunStatus.WaitingForApproval,
                    $"{pending.Status}: {pending.Failure?.Code}: {pending.Failure?.Summary}; diagnosticId={pending.Failure?.DiagnosticId}; modelRequests={model.Requests.Count}; diagnostics={SafePreparationDiagnostics(diagnostics)}" + Trace());
                var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
                if (index == (review ? 1 : 0))
                {
                    mutation = request; var material = request.Operation.ManagementMutation!.Resource!.Source!;
                    Assert.Equal(sourceName, material.Name); Assert.Equal(initial, material.RootRunId); Assert.Equal(review, material.ReadRequestId is not null);
                    Assert.Equal("resource-source", material.ToolCallId); if (source != "browser") Assert.NotNull(material.Workspace ?? (object?)material.Attachment);
                    Assert.Contains("no exact byte-copy claim", request.Operation.ManagementMutation.ChangesJson);
                }
                var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                Assert.True(decision.StatusCode == HttpStatusCode.OK, await decision.Content.ReadAsStringAsync() + Trace());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    var child = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.Admission.ApprovalResume?.RequestId == request.RequestId && r.AgentRunId != pending.AgentRunId);
                    if (child is not null && (child.IsTerminal || child.Status == AgentRunStatus.WaitingForApproval)) { done = child; runId = child.AgentRunId; break; }
                    await Task.Delay(25, deadline.Token);
                }
            }
            Assert.True(done.Status == AgentRunStatus.Completed,
                $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; diagnosticId={done.Failure?.DiagnosticId}; modelRequests={model.Requests.Count}; diagnostics={SafePreparationDiagnostics(diagnostics)}" + Trace());
            var receipt = await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, mutation!.Operation.OperationId); Assert.NotNull(receipt!.ManagementResource);
            Assert.Equal(5, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
            var pinRead = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.ToolCallId == "read-original-pin");
            Assert.Contains("ORIGINAL-PINNED-RESOURCE-TEXT", pinRead.Text); Assert.DoesNotContain("DERIVED-LITERAL-RESOURCE-DRAFT", pinRead.Text);
            Assert.Equal(4, done.Admission.Configuration!.InstanceRevision); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
            if (source is "resource" or "knowledge")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync(agentPath + "/resources/" + Uri.EscapeDataString(model.Key) + $"?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision=5&expectedResourceRevision=2")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/approvals/{mutation.RequestId}")).StatusCode);
                Assert.Equal(0, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
