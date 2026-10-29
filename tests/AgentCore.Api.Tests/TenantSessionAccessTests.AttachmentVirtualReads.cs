using System.Net;
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
    [Theory]
    [InlineData(false, "parent")] [InlineData(true, "parent")]
    [InlineData(false, "child")] [InlineData(true, "child")]
    [InlineData(false, "pending")] [InlineData(true, "pending")]
    [InlineData(false, "other-session")] [InlineData(true, "other-session")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "capability")] [InlineData(true, "capability")]
    [InlineData(false, "media")] [InlineData(true, "media")]
    [InlineData(false, "media-review")] [InlineData(true, "media-review")]
    public async Task Tenant_attachment_virtual_file_reader_withholds_source_when_current_boundary_denies(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-virtual-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tool = boundary is "media" or "media-review" ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.WorkspaceRead;
            var model = new AttachmentReadModel { ReadTool = tool };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
            void Configure(IServiceCollection services) => services.AddLogging(logs => logs.AddProvider(diagnostics));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: tool); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}"; var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var targetSession = v.Session;
            if (boundary != "pending")
            {
                var bound = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this original", [v.Id], await Revision(owner, v.Tenant), 1);
                try { Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, bound.RunId)).Status); }
                catch (Xunit.Sdk.XunitException e) { throw new Xunit.Sdk.XunitException(e.Message + "; diagnostics=" + string.Join("\n", diagnostics.Lines) + "; errors=" + string.Join("\n", diagnostics.Errors.Select(error => error.ToString()))); }
            }
            if (boundary == "other-session") targetSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new {
                retentionKind = "saved", agentInstanceId = value.Instance, expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            if (boundary is "parent" or "child")
            {
                var access = factory.Services.GetRequiredService<TenantAccessService>();
                await access.RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId, await Revision(owner, v.Tenant));
                var role = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Only one independent read", [boundary == "parent" ? "attachments.read" : "sessions.read"], await Revision(owner, v.Tenant));
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!;
                await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, state.SystemIdentities.Single(identity => identity.AgentInstanceId == value.Instance).PrincipalId, role.RoleId, AccessScope.ForTenant(v.Tenant), await Revision(owner, v.Tenant));
            }
            if (boundary is "review" or "media-review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/attachments.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (boundary == "capability") Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{value.Instance}/settings/capabilities", new {
                expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1, set = new { selected = new[] { tool } }, clear = Array.Empty<string>() })).StatusCode);
            model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = JsonSerializer.Serialize(new { path = "/attachments/" + v.Id.ToString("D") });
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, targetSession))!;
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{targetSession}/messages", new { messageId = Guid.NewGuid(), text = "Read the exact original",
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
            var done = await AttachmentTerminal(runs, v.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.Equal(2, model.Requests.Count);
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == tool);
            using var json = JsonDocument.Parse(result.Text); Assert.Equal("forbidden", json.RootElement.GetProperty("error").GetString());
            if (boundary == "media-review") Assert.Equal("attachment_read_review_required", json.RootElement.GetProperty("reasonCode").GetString());
            Assert.DoesNotContain("Private read body", result.Text); Assert.DoesNotContain("read.txt", result.Text); Assert.Null(result.Parts);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(v.Tenant, null, null, new(0, 100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "text", false, false)] [InlineData(true, "text", false, false)]
    [InlineData(false, "docx", false, false)] [InlineData(true, "docx", false, false)]
    [InlineData(false, "xlsx", false, false)] [InlineData(true, "xlsx", false, false)]
    [InlineData(false, "xlsx-range", false, false)] [InlineData(true, "xlsx-range", false, false)]
    [InlineData(false, "text", true, false)] [InlineData(true, "text", true, false)]
    [InlineData(false, "docx", true, false)] [InlineData(true, "docx", true, false)]
    [InlineData(false, "xlsx-range", true, false)] [InlineData(true, "xlsx-range", true, false)]
    [InlineData(false, "text", true, true)] [InlineData(true, "text", true, true)]
    [InlineData(false, "large-text", false, false)] [InlineData(true, "large-text", false, false)]
    public async Task Tenant_attachment_virtual_file_reader_uses_actual_tool_loop_and_canonical_exact_consent(bool sqlite, string format, bool review, bool rejected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-virtual-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tool = format == "docx" ? ToolCatalog.OfficeDocumentInspect : format.StartsWith("xlsx", StringComparison.Ordinal) ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.WorkspaceRead;
            var model = new AttachmentReadModel { ReadTool = tool, ReviewRead = review, HoldAfterRead = rejected || format == "large-text" };
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
            void Configure(IServiceCollection services) => services.AddLogging(logs => logs.AddProvider(diagnostics));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, format, virtualTool: tool); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}"; var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var bound = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, bound.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true;
            var path = "/attachments/" + v.Id.ToString("D");
            model.Arguments = format == "docx" ? JsonSerializer.Serialize(new { path, offset = 2, limit = 1 })
                : format == "xlsx-range" ? JsonSerializer.Serialize(new { path, sheet = "Data", range = "A1:B1" }) : JsonSerializer.Serialize(new { path });
            if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/attachments.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the original at " + path,
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
            var runId = sent.GetProperty("runId").GetGuid(); TenantApprovalRequest? request = null;
            if (format == "large-text")
            {
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var active = (await runs.FindTenantRunAsync(v.Tenant, runId))!;
                var reader = factory.Services.GetRequiredService<TenantAttachmentRuntimeContextService>();
                var frame = new TenantRuntimeFrame(active.Owner, v.Session, runId, active.Claim!.Generation);
                var boundedOutput = await reader.InspectVirtualAsync(frame, new("bounded-prefix", tool, model.Arguments), 4096);
                Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(boundedOutput), 1, 4096);
                using var page = JsonDocument.Parse(boundedOutput);
                Assert.True(page.RootElement.GetProperty("tenantAttachmentRead").GetBoolean());
                var inspection = page.RootElement.GetProperty("inspection"); Assert.True(inspection.GetProperty("truncated").GetBoolean());
                Assert.Contains("Private read body", inspection.GetProperty("content").GetString());
                foreach (var budget in new[] { 0, 1, 2, 128 })
                {
                    var tiny = await reader.InspectVirtualAsync(frame, new("tiny-budget", tool, model.Arguments), budget);
                    Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(tiny), 0, budget);
                    if (tiny.Length > 0) { using var valid = JsonDocument.Parse(tiny); }
                }
                return;
            }
            if (review)
            {
                request = await AwaitAttachmentReview(factory.Services, v.Tenant, runId);
                Assert.Null(request.Operation.WorkspaceRead); Assert.Equal(v.Id, request.Operation.AttachmentRead!.AttachmentId);
                Assert.True(TenantApprovalToolContract.Parse(new("exact-read", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new {
                    operation = "inspect_workspace", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(model.Arguments) } }))).Matches(request.Operation));
                Assert.Single(model.Requests); Assert.DoesNotContain("Private read body", string.Join("\n", model.Requests.Single().Messages.Select(m => m.Text)));
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal("read_attachment", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new {
                    decision = rejected ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            if (rejected)
            {
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var source = (await runs.FindTenantRunAsync(v.Tenant, runId))!;
                var successor = (await runs.FindTenantRunAsync(v.Tenant, source.Result!.ContinuationRunId!.Value))!;
                var duplicate = await factory.Services.GetRequiredService<TenantApprovalService>().PrepareAttachmentReadAsync(new(v.Tenant, successor.AgentRunId),
                    TenantAttachmentReadContract.Canonical(new(v.Id)), successor.Revision, successor.Claim!.Generation);
                Assert.Equal("previously_rejected", duplicate.ReasonCode); Assert.Equal(request!.RequestId, duplicate.Request!.RequestId);
                var viaVirtual = await factory.Services.GetRequiredService<TenantApprovalService>().PrepareWorkspaceReadAsync(new(v.Tenant, successor.AgentRunId),
                    new("retry-original-virtual-path", tool, model.Arguments), successor.Revision, successor.Claim.Generation);
                Assert.Equal("previously_rejected", viaVirtual.ReasonCode); Assert.Equal(request.RequestId, viaVirtual.Request!.RequestId);
                var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                Assert.Single((await approvals.ListApprovalsAsync(v.Tenant, null, null, new(0, 100))).Items);
                Assert.Null(await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(v.Tenant, request.Operation.OperationId));
                Assert.DoesNotContain("Private read body", string.Join("\n", model.Requests.Last().Messages.Select(m => m.Text)));
                return;
            }
            AgentRun done;
            try { done = review ? await AwaitBrowserRestartRun(runs, v.Tenant, runId, false) : await AttachmentTerminal(runs, v.Tenant, runId); }
            catch (Xunit.Sdk.XunitException e) { throw new Xunit.Sdk.XunitException(e.Message + "; diagnostics=" + string.Join("\n", diagnostics.Lines) + "; errors=" + string.Join("\n", diagnostics.Errors.Select(error => error.ToString()))); }
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; diagnostics={string.Join("\n", diagnostics.Lines)}"); Assert.Equal(2, model.Requests.Count);
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == (review ? ToolCatalog.RequestAuthorization : tool));
            using var json = JsonDocument.Parse(result.Text); var data = review ? json.RootElement.GetProperty("attachmentObservation") : json.RootElement;
            Assert.True(data.GetProperty("tenantAttachmentRead").GetBoolean()); Assert.Equal(v.Id, data.GetProperty("attachmentId").GetGuid());
            if (!review) { Assert.Equal(path, data.GetProperty("path").GetString()); Assert.Equal(tool, data.GetProperty("attachmentVirtualRead").GetProperty("Tool").GetString()); }
            Assert.Contains(format == "docx" ? "Second source paragraph" : "Private read body", result.Text);
            if (format == "docx") Assert.DoesNotContain("Private read body", result.Text);
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync(api + $"/sessions/{v.Session}/attachments/{v.Id}/content"));
            if (request is not null) Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(v.Tenant, request.RequestId));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
