using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "docx", "review")] [InlineData(true, "xlsx", "review")]
    [InlineData(true, "docx", "review")] [InlineData(false, "xlsx", "review")]
    [InlineData(false, "docx", "allowed")] [InlineData(true, "docx", "allowed")]
    [InlineData(false, "xlsx", "allowed")] [InlineData(true, "xlsx", "allowed")]
    [InlineData(false, "docx", "reject")] [InlineData(true, "xlsx", "reject")]
    [InlineData(false, "docx", "stale")] [InlineData(true, "xlsx", "stale")]
    [InlineData(false, "xlsx", "revoked")] [InlineData(true, "docx", "revoked")]
    [InlineData(true, "docx", "restart")] [InlineData(true, "xlsx", "restart")]
    [InlineData(false, "docx", "missing")] [InlineData(true, "xlsx", "missing")]
    [InlineData(false, "docx", "invalid")] [InlineData(true, "xlsx", "invalid")]
    [InlineData(false, "xlsx", "sparse")] [InlineData(true, "xlsx", "sparse")]
    [InlineData(false, "xlsx", "range-first")]
    [InlineData(false, "xlsx", "cursor-invalid")] [InlineData(true, "xlsx", "cursor-changed")]
    [InlineData(false, "docx", "oversized")] [InlineData(true, "xlsx", "oversized")]
    public async Task Tenant_Office_model_reads_use_canonical_bytes_and_exact_saved_read_consent(bool sqlite, string format, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-office-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            var proof = !sqlite && format == "docx" && mode == "review" && System.Environment.GetEnvironmentVariable("AGENTCORE_OFFICE_READ_UI_PROOF") == "1";
            const string proofPath = "/private/tmp/agent-core-office-read-ui";
            var reviewed = mode is "review" or "reject" or "stale" or "revoked" or "restart" or "range-first";
            var sparse = mode is "sparse" or "cursor-invalid" or "cursor-changed";
            var working = format == "xlsx"; var path = (working ? "/working/" : "/home/") + (proof ? "quarterly-Office-inspection-with-long-name-thử-nghiệm-試験-2026." : "exact.") + format;
            var tool = working ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.OfficeDocumentInspect;
            var json = sparse ? JsonSerializer.Serialize(new { path }) : mode == "range-first" ? JsonSerializer.Serialize(new { path, range = "A1:B2" }) : working ? JsonSerializer.Serialize(new { path, sheet = "試験", range = "A1:B2" }) : JsonSerializer.Serialize(new { path, offset = 1, limit = 2 });
            var model = new TenantOfficeModel(tool, json, reviewed) { ContinueSparse = sparse, InvalidCursor = mode == "cursor-invalid" };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
            void Configure(IServiceCollection services) { services.AddLogging(logging => logging.AddProvider(diagnostics)); if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"]; }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initial; if (proof) factory.UseKestrel(0);
            using var firstOwner = factory.CreateClient(); HttpClient owner = firstOwner;
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            const string body = "Private Office content thử nghiệm";
            var processor = factory.Services.GetRequiredService<IOfficeProcessor>();
            var bytes = working ? processor.CreateWorkbook(new([new("試験", [[new("text", Text: body), new("number", Number: 7)]]), new("Second", [[new("text", Text: "Second sheet private content")]])]), default).Bytes
                : processor.CreateDocument(new("Private title", [new(null, [body, "Second exact paragraph", "Outside requested excerpt"], null)]), default).Bytes;
            if (mode == "invalid") bytes = "Not an Office package"u8.ToArray();
            if (mode == "oversized") bytes = new byte[OfficeLimits.MaxPackageBytes + 1];
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Office source reader", mode == "missing" ? ["agents.read", "sessions.read"] : ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (reviewed) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (mode == "cursor-changed") model.BeforeContinuation = async () =>
            {
                bytes = processor.CreateWorkbook(new([new("試験", [[new("text", Text: "Changed source")]]), new("Second", [[new("text", Text: "Changed second sheet")]])]), default).Bytes;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
            };
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the exact saved Office excerpt", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (reviewed)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                if (mode == "restart")
                {
                    await factory.DisposeAsync(); model = new TenantOfficeModel(tool, json, true);
                    recreated = new NativeTenantSqliteFactory(root, model); recreatedOwner = recreated.CreateClient(); factory = recreated; owner = recreatedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, runId))!.Status);
                }
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(tool, request.Operation.WorkspaceRead!.ToolName);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.DoesNotContain(body, detail.GetRawText());
                Assert.DoesNotContain(request.Operation.WorkspaceRead.ObservationHash, detail.GetRawText());
                Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "readPage"
                    && c.GetProperty("after").GetString()!.Contains(working ? "A1:B2" : "offset 1; up to 2 elements"));
                if (mode == "range-first") Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "readPage" && c.GetProperty("after").GetString()!.Contains("first worksheet"));
                if (mode is "stale" or "revoked")
                {
                    if (mode == "stale")
                    {
                        bytes = working ? processor.CreateWorkbook(new([new("試験", [[new("text", Text: "Changed exact source")]])]), default).Bytes
                            : processor.CreateDocument(new("Changed title", [new(null, ["Changed exact source"], null)]), default).Bytes;
                        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
                    }
                    else await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                    detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                }
                else if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, instance, session, requestId = request.RequestId, path }));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-decided")) await Task.Delay(100, deadline.Token);
                }
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, runId, false); Assert.True(done.Status == AgentRunStatus.Completed,
                $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; modelRequests={model.Requests.Count}; diagnosticId={done.Failure?.DiagnosticId}; diagnostics={SafePreparationDiagnostics(diagnostics)}");
            Assert.Equal(bytes, await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path)));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            var result = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == (reviewed ? ToolCatalog.RequestAuthorization : tool));
            using var output = JsonDocument.Parse(result.Text);
            if (mode is "reject" or "stale" or "revoked")
            {
                Assert.False(output.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(output.RootElement.GetProperty("applied").GetBoolean());
                Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("workspaceObservation").ValueKind); Assert.DoesNotContain(body, result.Text); return;
            }
            if (mode is "missing" or "invalid" or "oversized" or "cursor-invalid" or "cursor-changed")
            {
                var code = mode switch { "missing" => "executor_read_required", "invalid" => "invalid_office_package", "oversized" => "office_processing_limit", "cursor-invalid" => "office_cursor_invalid", _ => "office_revision_conflict" };
                Assert.Equal(code, output.RootElement.TryGetProperty("reasonCode", out var reason) ? reason.GetString() : output.RootElement.GetProperty("error").GetString());
                Assert.DoesNotContain(body, result.Text); return;
            }
            var data = reviewed ? output.RootElement.GetProperty("workspaceObservation") : output.RootElement;
            Assert.True(data.GetProperty("tenantWorkspaceRead").GetBoolean()); Assert.True(data.GetProperty("untrusted").GetBoolean());
            var inspection = data.GetProperty("inspection");
            if (sparse)
            {
                Assert.Equal("sparse", inspection.GetProperty("mode").GetString()); Assert.Equal("Second", inspection.GetProperty("sheet").GetString());
                Assert.Contains(inspection.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("value").GetString() == "Second sheet private content");
                Assert.Equal(2, model.Requests.Last().Messages.Count(m => m.Role == ModelRole.Tool && m.Name == tool));
            }
            else if (working) Assert.Equal(body, inspection.GetProperty("rows")[0][0].GetProperty("value").GetString());
            else Assert.Contains(inspection.GetProperty("elements").EnumerateArray(), e => e.GetProperty("text").GetString() == body);
            if (working && !sparse) { Assert.Equal("range", inspection.GetProperty("mode").GetString()); Assert.Equal("試験", inspection.GetProperty("sheet").GetString()); }
            else if (!working) { Assert.Equal(1, inspection.GetProperty("offset").GetInt32()); Assert.Equal(2, inspection.GetProperty("elements").GetArrayLength()); Assert.DoesNotContain("Outside requested excerpt", inspection.GetRawText()); }
            Assert.Equal(bytes, await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path)));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{}");
                var secondSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var secondWorkspace = api + $"/sessions/{secondSession}/workspace";
                var secondPath = "/working/quarterly-spreadsheet-inspection-with-long-name-thử-nghiệm-試験-2026.xlsx";
                const string sheet = "Quarterly results 試験 thử";
                var secondBytes = processor.CreateWorkbook(new([new(sheet, [[new("text", Text: "Private second source")]])]), default).Bytes;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(secondWorkspace + $"/content?path={Uri.EscapeDataString(secondPath)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(secondBytes))).StatusCode);
                model.Select(ToolCatalog.OfficeSpreadsheetInspect, JsonSerializer.Serialize(new { path = secondPath, sheet, range = "A1:B2" }));
                var secondSent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{secondSession}/messages", new { messageId = Guid.NewGuid(), text = "Review the exact spreadsheet range", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var secondId = secondSent.GetProperty("runId").GetGuid(); var secondWaiting = await AwaitBrowserRestartRun(runs, tenant, secondId, true);
                var secondRequest = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, secondWaiting.ApprovalRequestId!.Value))!;
                await File.WriteAllTextAsync(proofPath + "-xlsx.json", JsonSerializer.Serialize(new { tenant, session = secondSession, requestId = secondRequest.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-xlsx-reviewed")) await Task.Delay(100, deadline.Token);
                var changed = processor.CreateWorkbook(new([new(sheet, [[new("text", Text: "Changed second source")]])]), default).Bytes;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(secondWorkspace + $"/content?path={Uri.EscapeDataString(secondPath)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(changed))).StatusCode);
                var stale = await Json(await owner.GetAsync(api + $"/approvals/{secondRequest.RequestId}")); Assert.False(stale.GetProperty("review").GetProperty("canDecide").GetBoolean());
                await File.WriteAllTextAsync(proofPath + "-stale.json", "{}");
                while (!File.Exists(proofPath + "-stale-cancelled")) await Task.Delay(100, deadline.Token);
                var secondDone = await AwaitBrowserRestartRun(runs, tenant, secondId, false); Assert.Equal(AgentRunStatus.Completed, secondDone.Status);
                var refused = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
                using var refusedJson = JsonDocument.Parse(refused); Assert.False(refusedJson.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.Equal(JsonValueKind.Null, refusedJson.RootElement.GetProperty("workspaceObservation").ValueKind);
                Assert.Equal(changed, await owner.GetByteArrayAsync(secondWorkspace + "/content?path=" + Uri.EscapeDataString(secondPath)));
                await File.WriteAllTextAsync(proofPath + "-finished.json", "{}");
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static string SafePreparationDiagnostics(DiagnosticLineLoggerProvider diagnostics)
    {
        lock (diagnostics.Lines)
        {
            static string Bound(string text) => text[..Math.Min(1024, text.Length)];
            static string Cause(Exception error) => error.GetType().Assembly == typeof(DiagnosticLog).Assembly
                && error.GetType().DeclaringType?.FullName == "AgentCore.Application.Observability.DiagnosticExceptionSnapshot"
                && error.GetType().Name is "SafeException" or "SafeAggregateException"
                ? error.ToString() : $"{error.GetType().FullName} HResult={error.HResult}";
            // DiagnosticExceptionSnapshot captures content-free type/code/stack evidence.
            // Do not render ordinary exception messages or unrelated warning text.
            var handled = diagnostics.Lines.Where(line =>
                line.StartsWith("The agent could not prepare a response.", StringComparison.Ordinal)
                || line.StartsWith("Tenant Run projection unavailable.", StringComparison.Ordinal)
                || line.StartsWith("Language model pump failed.", StringComparison.Ordinal)
                || line.StartsWith("Assistant response failed.", StringComparison.Ordinal));
            return string.Join(" | ", handled.Concat(diagnostics.Errors.Select(Cause)).TakeLast(16).Select(Bound));
        }
    }

    private sealed class TenantOfficeModel(string tool, string arguments, bool reviewed) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(); public ModelCapabilities Capabilities => inner.Capabilities;
        public void Select(string nextTool, string nextArguments) { tool = nextTool; arguments = nextArguments; }
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public bool ContinueSparse { get; init; }
        public bool InvalidCursor { get; init; }
        public Func<Task>? BeforeContinuation { get; set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); var name = reviewed ? ToolCatalog.RequestAuthorization : tool;
            var prior = request.Messages.Where(m => m.Role == ModelRole.Tool && m.Name == name).ToArray();
            string? nextArguments = null;
            if (ContinueSparse && prior.Length == 1)
            {
                using var priorJson = JsonDocument.Parse(prior[0].Text);
                if (priorJson.RootElement.TryGetProperty("inspection", out var page) && page.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
                {
                    if (BeforeContinuation is not null) await BeforeContinuation();
                    using var original = JsonDocument.Parse(arguments);
                    nextArguments = JsonSerializer.Serialize(new { path = original.RootElement.GetProperty("path").GetString(), cursor = InvalidCursor ? "invalid-cursor" : cursor.GetString() });
                }
            }
            if (prior.Length == 0 || nextArguments is not null)
            {
                Assert.Contains(request.Tools!, t => t.Name == tool);
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(tool));
                yield return new ModelToolCallEvent(new("exact-office-" + prior.Length, name, reviewed ? JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } }) : nextArguments ?? arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
}
