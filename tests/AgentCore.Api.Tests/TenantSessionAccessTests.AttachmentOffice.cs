using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Office;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_attachment_HTTP_Office_preview_preserves_package_without_workspace_or_model_processing(bool sqlite, bool workbook)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-office-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) })); var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var office = factory.Services.GetRequiredService<IOfficeProcessor>();
            var bytes = workbook ? office.CreateWorkbook(new([new("Dữ liệu", [[new("number", Number: 0), new("text", Text: "Private Office upload")]])]), default).Bytes
                : office.CreateDocument(new("Private Office upload", [new(null, ["Private Office upload: thử nghiệm"], null)]), default).Bytes;
            var path = api + $"/sessions/{session}/attachments"; using var body = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(workbook ? OfficeLimits.SpreadsheetMime : OfficeLimits.DocumentMime); body.Add(file, "file", workbook ? "source.xlsx" : "source.docx");
            var response = await owner.PostAsync(path + $"?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await Json(response)).GetProperty("attachmentId").GetGuid(); var detail = await Json(await owner.GetAsync(path + $"/{id}")); Assert.Equal("pending", detail.GetProperty("state").GetString());
            var preview = await owner.GetAsync(path + $"/{id}/office-preview"); Assert.Equal(HttpStatusCode.OK, preview.StatusCode); var data = await Json(preview); Assert.Equal(workbook ? "xlsx" : "docx", data.GetProperty("format").GetString());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), data.GetProperty("sha256").GetString());
            if (workbook) { Assert.Equal("0", data.GetProperty("rows")[0][0].GetProperty("value").GetString()); Assert.Equal("Private Office upload", data.GetProperty("rows")[0][1].GetProperty("value").GetString()); }
            else Assert.False(data.GetProperty("emptyBody").GetBoolean());
            Assert.Equal(bytes, await owner.GetByteArrayAsync(path + $"/{id}/content")); Assert.Empty(model.Requests); Assert.False(Directory.Exists(AgentCore.Infrastructure.Workspaces.AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(root, "workspaces"), instance)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
