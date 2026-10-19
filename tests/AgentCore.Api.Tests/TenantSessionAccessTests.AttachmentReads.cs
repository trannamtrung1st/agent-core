using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class AttachmentReadModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public bool ReadEnabled { get; set; }
        public bool ReviewRead { get; set; }
        public bool ReadSubmittedSelector { get; init; }
        public bool HoldAfterRead { get; init; }
        public string Arguments { get; set; } = "{}";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            var tool = ReviewRead ? ToolCatalog.RequestAuthorization : ToolCatalog.AttachmentsRead;
            if (ReadEnabled && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == tool))
            {
                Assert.Contains(request.Tools!, t => t.Name == tool);
                var arguments = Arguments;
                if (ReadSubmittedSelector)
                {
                    const string marker = "[User-provided attachment references (identifiers only; access has not been established): ";
                    var submitted = request.Messages.Where(m => m.Role == ModelRole.User).Select(m => m.Text).Last(t => t.Contains(marker, StringComparison.Ordinal));
                    var references = submitted[(submitted.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split(']')[0];
                    var id = Guid.Parse(Assert.Single(references.Split(", ")));
                    var input = System.Text.Json.Nodes.JsonNode.Parse(arguments)!.AsObject(); input["attachmentId"] = id.ToString("D"); arguments = input.ToJsonString();
                }
                var args = ReviewRead ? JsonSerializer.Serialize(new { operation = "read_attachment", read = JsonSerializer.Deserialize<JsonElement>(arguments) }) : arguments;
                yield return new ModelToolCallEvent(new("read-bound-source", tool, args));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ReadEnabled) { Entered.TrySetResult(); if (HoldAfterRead) await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
        }
    }
    private sealed record AttachmentReadCase(AttachmentExecutionCase Execution, byte[] Bytes, Guid Instance);
    private static async Task<AttachmentReadCase> PrepareAttachmentRead(IServiceProvider services, HttpClient owner, string format, bool recoveryBudget = false, bool explicitCapability = false, string? displayNameOverride = null)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        JsonElement agent;
        if (explicitCapability)
        {
            var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Attachment reader", expectedRevision = await Revision(owner, tenant) }));
            var draftId = draft.GetProperty("draftId").GetGuid();
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
            candidate["environment"] = JsonSerializer.SerializeToNode(new {
                harness = Array.Empty<string>(), knowledgeSources = Array.Empty<string>(), toolAllowlist = Array.Empty<string>(),
                capabilities = new { mode = "Selected", resolvedCapabilities = new[] { ToolCatalog.AttachmentsRead } },
                projection = new { alwaysCapabilities = Array.Empty<string>() } });
            var saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
            await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
            agent = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }));
        }
        else agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var instanceRevision = 1L;
        if (recoveryBudget)
        {
            var changed = await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                budgets = new AgentCore.Domain.Definitions.ExecutionBudgetPolicy(Standard: new(24, 600), InteractiveBrowser: new(48, 600), UnattendedBoundBrowser: new(32, 600)) }));
            instanceRevision = changed.GetProperty("instance").GetProperty("revision").GetInt64();
        }
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
            modelKey = format == "png" ? "scripted-vision" : null }))).GetProperty("sessionId").GetGuid();
        byte[] bytes; string name, type; var office = services.GetRequiredService<IOfficeProcessor>();
        if (format == "docx") { bytes = office.CreateDocument(new("Read document", [new(null, ["Private read body", "Second source paragraph"], null)]), default).Bytes; name = "read.docx"; type = OfficeLimits.DocumentMime; }
        else if (format.StartsWith("xlsx", StringComparison.Ordinal)) { bytes = office.CreateWorkbook(new([new("Data", [[new("number", Number: 0), new("text", Text: "Private read body")]])]), default).Bytes; name = "read.xlsx"; type = OfficeLimits.SpreadsheetMime; }
        else if (format == "pdf") { bytes = "%PDF-1.7\n1 0 obj << /Type /Page >> endobj\nBT (Private read body) Tj ET\n%%EOF"u8.ToArray(); name = "read.pdf"; type = "application/pdf"; }
        else if (format.StartsWith("png", StringComparison.Ordinal)) { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red); using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); bytes = encoded.ToArray(); name = "read.png"; type = "image/png"; }
        else if (format == "binary") { bytes = [0, 1, 2, 255, 254]; name = "read.bin"; type = "application/octet-stream"; }
        else { bytes = "Private read body → thử nghiệm"u8.ToArray(); name = "read.txt"; type = "text/plain"; }
        name = displayNameOverride ?? name;
        using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", name);
        var id = (await Json(await owner.PostAsync(api + $"/sessions/{session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
        var login = (await services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
        var state = (await services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single(); var access = services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human.PrincipalId, tenant, null, "Agent exact source reads", ["sessions.read", "attachments.read"], state.Tenant.Revision);
        var grant = await access.AssignRoleAsync(human.PrincipalId, tenant, agent.GetProperty("principalId").GetGuid(), role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        return new(new(tenant, session, id, new(human.AccountId, login.Session.SessionId, human.PrincipalId), grant), bytes, instance);
    }
    [Theory] [InlineData(false, "text")] [InlineData(true, "text")]
    [InlineData(false, "docx")] [InlineData(true, "docx")] [InlineData(false, "xlsx")] [InlineData(true, "xlsx")]
    [InlineData(false, "xlsx-range")] [InlineData(true, "xlsx-range")] [InlineData(false, "pdf")] [InlineData(true, "pdf")]
    [InlineData(false, "png")] [InlineData(true, "png")] [InlineData(false, "png-no-vision")] [InlineData(true, "png-no-vision")]
    [InlineData(false, "binary")] [InlineData(true, "binary")]
    public async Task Tenant_attachment_model_reads_historical_source_through_actual_tool_loop_and_keeps_original_bytes(bool sqlite, string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-model-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, format); var v = value.Execution;
            var bound = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, bound.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true;
            model.Arguments = format == "docx" ? JsonSerializer.Serialize(new { attachmentId = v.Id, offset = 0, limit = 1 })
                : format == "xlsx-range" ? JsonSerializer.Serialize(new { attachmentId = v.Id, sheet = "Data", range = "A1:B1" }) : JsonSerializer.Serialize(new { attachmentId = v.Id });
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                messageId = Guid.NewGuid(), text = "Read that earlier file", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
            var run = await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}; requests={model.Requests.Count}; tools={string.Join(",", model.Requests.FirstOrDefault()?.Tools?.Select(t => t.Name) ?? [])}"); Assert.Equal(2, model.Requests.Count);
            var first = model.Requests.First(); Assert.DoesNotContain("Private read body", string.Join("\n", first.Messages.Select(m => m.Text)));
            Assert.Contains(first.Tools!, t => t.Name == ToolCatalog.AttachmentsRead && t.Description.Contains("current Agent Session/Attachment"));
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AttachmentsRead);
            using var doc = JsonDocument.Parse(result.Text); Assert.True(doc.RootElement.GetProperty("tenantAttachmentRead").GetBoolean());
            Assert.Equal(v.Id, doc.RootElement.GetProperty("attachmentId").GetGuid());
            if (format == "png") { var image = Assert.Single(result.Parts!.OfType<ModelImageContent>()); using var bitmap = SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, bitmap.Width); Assert.Equal(2, bitmap.Height); }
            else
            {
                Assert.True(result.Parts is null or { Count: 0 });
                if (format is "binary" or "png-no-vision") Assert.False(doc.RootElement.GetProperty("inspection").GetProperty("contentProvided").GetBoolean());
                else if (format == "docx") { Assert.True(doc.RootElement.GetProperty("inspection").GetProperty("truncated").GetBoolean()); Assert.Equal(1, doc.RootElement.GetProperty("inspection").GetProperty("nextOffset").GetInt32()); }
                else Assert.Contains("Private read body", result.Text);
            }
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/attachments/{v.Id}/content"));
            Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), run.Checkpoint!.PayloadJson);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
