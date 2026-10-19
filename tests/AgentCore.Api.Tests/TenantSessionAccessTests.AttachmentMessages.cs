using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "attachments.read")] [InlineData(true, "attachments.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    public async Task Tenant_public_file_only_message_preserves_submitted_selector_and_exact_Review_without_disclosing_source_before_consent(bool sqlite, string permission)
    {
        if (!sqlite && permission == "attachments.read" && Environment.GetEnvironmentVariable("AGENTCORE_ATTACHMENT_MESSAGE_UI_PROOF") == "1")
        { await AttachmentComposerProof(); return; }
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-message-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { ReadEnabled = true, ReviewRead = true, ReadSubmittedSelector = true };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text"); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            var message = Guid.NewGuid();
            var posted = await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new {
                messageId = message, text = "", attachmentIds = new[] { v.Id }, expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode); var sent = await Json(posted); var source = sent.GetProperty("runId").GetGuid();
            var request = await AwaitAttachmentReview(factory.Services, v.Tenant, source); Assert.Equal(v.Id, request.Operation.AttachmentRead!.AttachmentId);
            var initial = Assert.Single(model.Requests); var userText = string.Join("\n", initial.Messages.Where(m => m.Role == ModelRole.User).Select(m => m.Text));
            Assert.Contains(v.Id.ToString("D"), userText); Assert.Contains("identifiers only; access has not been established", userText);
            Assert.DoesNotContain("Private read body", userText); Assert.DoesNotContain("read.txt", userText);
            var files = factory.Services.GetRequiredService<ITenantAttachmentStore>(); var bound = (await files.FindTenantAsync(v.Tenant, v.Session, v.Id))!;
            Assert.Equal(AttachmentState.Bound, bound.State); Assert.Equal(sent.GetProperty("entryId").GetGuid(), bound.EntryId);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new {
                decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, source, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var data = JsonDocument.Parse(result.Text); Assert.True(data.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.Contains("Private read body", result.Text);
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(v.Tenant, request.RequestId));
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var duplicate = await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new {
                messageId = message, text = "", attachmentIds = new[] { v.Id }, expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision });
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); Assert.Equal(source, (await Json(duplicate)).GetProperty("runId").GetGuid());
            var beforeReplay = model.Requests.Count;
            var changed = await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new {
                messageId = message, text = "Changed intent", attachmentIds = new[] { v.Id }, expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision });
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode); Assert.Equal(beforeReplay, model.Requests.Count);
            Assert.Equal(bound, await files.FindTenantAsync(v.Tenant, v.Session, v.Id));
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync(api + $"/sessions/{v.Session}/attachments/{v.Id}/content"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_public_file_message_HTTP_rejects_malformed_or_unavailable_selectors_without_binding_or_admitting_work(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-message-invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text"); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}";
            foreach (var ids in new[] { new[] { Guid.Empty }, new[] { v.Id, v.Id }, Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray() })
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + "/messages", new { messageId = Guid.NewGuid(), text = "Unadmitted", attachmentIds = ids,
                    expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + "/messages", new { messageId = Guid.NewGuid(), text = "Unadmitted", attachmentIds = new[] { Guid.NewGuid() },
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + "/messages", new { messageId = Guid.NewGuid(), text = "Unadmitted", attachmentIds = new[] { "not-a-uuid" },
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + "/messages", new { messageId = Guid.NewGuid(), text = (string?)null, attachmentIds = new[] { v.Id },
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
            var source = (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(v.Tenant, v.Session, v.Id))!;
            Assert.Equal(AttachmentState.Pending, source.State); Assert.Null(source.EntryId); Assert.Empty(model.Requests);
            Assert.Empty((await Json(await owner.GetAsync(api + "/messages"))).GetProperty("items").EnumerateArray());
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
