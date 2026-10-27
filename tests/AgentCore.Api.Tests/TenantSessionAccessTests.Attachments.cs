using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Tenant_attachment_HTTP_upload_exact_download_retry_abort_and_restart_use_actual_owner(bool sqlite, bool restart)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? fresh = null;
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            WebApplicationFactory<Program> factory = initial; using var first = factory.CreateClient(); var owner = first; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var path = api + $"/sessions/{session}/attachments"; var revision = await Revision(owner, tenant); var uploadId = Guid.NewGuid();
            var bytes = "Private upload: thử nghiệm"u8.ToArray();
            async Task<HttpResponseMessage> Upload(byte[] data)
            {
                using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(data); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); body.Add(file, "file", "source.txt");
                return await owner.PostAsync(path + $"?uploadId={uploadId}&expectedRevision={revision}&expectedSessionRevision=1", body);
            }
            var response = await Upload(bytes); Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync()); var ack = await Json(response); Assert.Single(ack.EnumerateObject()); var id = ack.GetProperty("attachmentId").GetGuid();
            Assert.Equal(id, (await Json(await Upload(bytes))).GetProperty("attachmentId").GetGuid());
            Assert.Equal(HttpStatusCode.Conflict, (await Upload("Changed bytes"u8.ToArray())).StatusCode);
            if (restart)
            {
                await initial.DisposeAsync(); recreated = new NativeTenantSqliteFactory(root, model); factory = recreated; fresh = factory.CreateClient(); owner = fresh;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(id, (await Json(await Upload(bytes))).GetProperty("attachmentId").GetGuid());
            }
            var detail = await Json(await owner.GetAsync(path + $"/{id}")); Assert.Equal(session, detail.GetProperty("sessionId").GetGuid()); Assert.DoesNotContain("blobKey", detail.GetRawText()); Assert.DoesNotContain("uploadedByPrincipalId", detail.GetRawText());
            Assert.Equal(bytes, await owner.GetByteArrayAsync(path + $"/{id}/content")); Assert.Single((await Json(await owner.GetAsync(path + "?limit=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{Guid.NewGuid()}/attachments/{id}/content")).StatusCode);
            var request = Guid.NewGuid(); var abort = path + $"/{id}?requestId={request}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(abort)).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(abort)).StatusCode);
            Assert.Empty((await Json(await owner.GetAsync(path))).GetProperty("items").EnumerateArray()); Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + $"/{id}/content")).StatusCode);
            Assert.True(await factory.Services.GetRequiredService<TenantAttachmentCleanupService>().RecoverAsync() >= 1);
            if (sqlite) Assert.False(File.Exists(Path.Combine(root, "attachments", "tenants", tenant.ToString("N"), session.ToString("N"), id.ToString("N"))));
            Assert.Empty(model.Requests);
        }
        finally { fresh?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
