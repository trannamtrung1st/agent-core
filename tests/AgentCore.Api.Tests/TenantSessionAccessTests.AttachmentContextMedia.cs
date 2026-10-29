using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "docx")] [InlineData(true, "docx")] [InlineData(false, "xlsx")] [InlineData(true, "xlsx")]
    [InlineData(false, "pdf")] [InlineData(true, "pdf")] [InlineData(false, "png")] [InlineData(true, "png")]
    [InlineData(false, "png-no-vision")] [InlineData(true, "png-no-vision")] [InlineData(false, "binary")] [InlineData(true, "binary")]
    public async Task Tenant_attachment_context_reuses_guarded_media_processors_and_preserves_original_downloads(bool sqlite, string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-context-media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                modelPreference = format == "png" ? new { mode = "fixed", offeringId = "dca30000-0000-4000-8000-000000000003" } : null }))).GetProperty("sessionId").GetGuid();
            var office = factory.Services.GetRequiredService<IOfficeProcessor>(); byte[] bytes; string name, type;
            if (format == "docx") { bytes = office.CreateDocument(new("Private Office input", [new(null, ["Private Office input"], null)]), default).Bytes; name = "source.docx"; type = OfficeLimits.DocumentMime; }
            else if (format == "xlsx") { bytes = office.CreateWorkbook(new([new("Data", [[new("number", Number: 0), new("text", Text: "Private Office input")]])]), default).Bytes; name = "source.xlsx"; type = OfficeLimits.SpreadsheetMime; }
            else if (format == "pdf") { bytes = "%PDF-1.7\n1 0 obj << /Type /Page >> endobj\nBT (Private PDF input) Tj ET\n%%EOF"u8.ToArray(); name = "source.pdf"; type = "application/pdf"; }
            else if (format.StartsWith("png", StringComparison.Ordinal)) { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Blue); using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); bytes = encoded.ToArray(); name = "source.png"; type = "image/png"; }
            else { bytes = [0, 1, 2, 255, 254]; name = "source.bin"; type = "application/octet-stream"; }
            var path = api + $"/sessions/{session}/attachments"; using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", name);
            var id = (await Json(await owner.PostAsync(path + $"?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human.PrincipalId, tenant, null, "Agent media reads", ["sessions.read", "attachments.read"], state.Tenant.Revision);
            await access.AssignRoleAsync(human.PrincipalId, tenant, agent.GetProperty("principalId").GetGuid(), role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var service = factory.Services.GetRequiredService<TenantRunService>();
            var admissionRevision = await Revision(owner, tenant); var messageId = Guid.NewGuid();
            Task<TenantRunReceipt> Submit() => service.SubmitMessageAsync(tenant, session,
                new(human.AccountId, login.Session.SessionId, human.PrincipalId), messageId, "Inspect the provided file", [id], admissionRevision, 1).AsTask();
            if (format == "png-no-vision")
            {
                var refusal = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(Submit);
                Assert.Equal("ModelCapabilityUnsupported", refusal.Code);
                Assert.Empty(model.Requests);
                Assert.Empty(await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session));
                var pending = (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(tenant, session, id))!;
                Assert.Equal(AttachmentState.Pending, pending.State);
                Assert.Equal(bytes, await owner.GetByteArrayAsync(path + $"/{id}/content"));
                Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
                return;
            }
            var receipt = await Submit();
            AgentRun? run = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!timeout.IsCancellationRequested) { run = await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, receipt.RunId, timeout.Token); if (run?.IsTerminal == true) break; await Task.Delay(50, timeout.Token); }
            Assert.NotNull(run); Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}"); Assert.NotEmpty(model.Requests);
            var prompt = string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text + "\n" + string.Join("\n", (m.Parts ?? []).OfType<ModelTextContent>().Select(p => p.Text))));
            Assert.Contains(name, prompt);
            var images = model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Parts ?? []).OfType<ModelImageContent>().ToArray();
            if (format == "png") { var image = Assert.Single(images); Assert.Equal("image/png", image.ContentType); using var bitmap = SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, bitmap.Width); Assert.Equal(2, bitmap.Height); }
            else { Assert.Empty(images); if (format is "docx" or "xlsx") Assert.Contains("Private Office input", prompt); else if (format == "pdf") Assert.Contains("Private PDF input", prompt); else Assert.Contains("no automatic content reader", prompt); }
            Assert.Equal(bytes, await owner.GetByteArrayAsync(path + $"/{id}/content")); Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(root, "workspaces"), instance)));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
