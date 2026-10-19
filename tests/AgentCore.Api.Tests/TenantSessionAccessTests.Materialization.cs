using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Human_materialization_HTTP_preserves_original_creates_exclusive_working_and_immutable_artifact_without_Run(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var sessionPath = api + $"/sessions/{session}";
            var bytes = "Exact original thử nghiệm"u8.ToArray();
            using var form = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); form.Add(file, "file", "source.txt");
            var upload = await Json(await owner.PostAsync(sessionPath + $"/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", form));
            var attachment = upload.GetProperty("attachmentId").GetGuid(); var url = sessionPath + $"/attachments/{attachment}/materialize";
            for (var copy = 1; copy <= 2; copy++)
            {
                var response = await owner.PostAsJsonAsync(url, new { expectedRevision = await Revision(owner, tenant) });
                Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
                var ack = await Json(response); Assert.Equal(2, ack.EnumerateObject().Count()); var id = ack.GetProperty("artifactId").GetGuid();
                var path = copy == 1 ? "/working/source.txt" : "/working/source-2.txt"; Assert.Equal(path, ack.GetProperty("workspaceLogicalPath").GetString());
                Assert.Equal(bytes, await owner.GetByteArrayAsync(sessionPath + "/workspace/content?path=" + Uri.EscapeDataString(path)));
                Assert.Equal(bytes, await owner.GetByteArrayAsync(sessionPath + $"/artifacts/{id}/content"));
                var saved = await factory.Services.GetRequiredService<ITenantArtifactStore>().FindTenantAsync(tenant, session, id); Assert.NotNull(saved);
                Assert.Null(saved.AgentRunId); Assert.NotNull(saved.CreatedByPrincipalId); Assert.Equal(attachment, saved.SourceAttachmentId); Assert.Equal(path, saved.WorkspaceLogicalPath);
                Assert.Equal(bytes, await owner.GetByteArrayAsync(sessionPath + $"/attachments/{attachment}/content"));
            }
            Assert.Equal(2, (await Json(await owner.GetAsync(sessionPath + "/artifacts"))).GetProperty("items").GetArrayLength());
            Assert.Empty(await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session)); Assert.Empty(model.Requests);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
