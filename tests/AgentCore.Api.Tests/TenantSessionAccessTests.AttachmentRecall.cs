using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "text")] [InlineData(true, "text")]
    [InlineData(false, "png")] [InlineData(true, "png")]
    public async Task Tenant_paused_Session_retains_attachment_references_and_reads_earlier_file_after_explicit_resume(bool sqlite, string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-recall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? first = null, second = null;
        try
        {
            var initial = new AttachmentReadModel();
            first = sqlite ? new NativeTenantSqliteFactory(root, initial) : new NativeTenantMemoryFactory(root, initial);
            AttachmentReadCase value;
            using (var owner = first.CreateClient())
            {
                value = await PrepareAttachmentRead(first.Services, owner, format); var v = value.Execution;
                var admitted = await first.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human,
                    Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(first.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, admitted.RunId)).Status);
                var saved = (await first.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                var history = (await Json(await owner.GetAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages"))).GetProperty("items");
                var user = Assert.Single(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "user");
                Assert.Equal(v.Id, Assert.Single(user.GetProperty("attachments").EnumerateArray()).GetProperty("attachmentId").GetGuid());
                var paused = await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/pause", new {
                    expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision });
                Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
                Assert.Equal("paused", (await Json(paused)).GetProperty("lifecycleStatus").GetString());
            }
            var model = sqlite ? new AttachmentReadModel() : initial;
            if (sqlite)
            {
                await first.DisposeAsync(); first = null;
                second = new NativeTenantSqliteFactory(root, model);
            }
            var factory = second ?? first!; var e = value.Execution;
            using var again = factory.CreateClient(); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var retained = (await sessions.FindTenantSessionAsync(e.Tenant, e.Session))!;
            Assert.Equal(SessionLifecycleStatus.Paused, retained.Snapshot.LifecycleStatus);
            var resumed = await again.PostAsJsonAsync($"/api/v2/tenants/{e.Tenant}/sessions/{e.Session}/resume", new {
                expectedRevision = await Revision(again, e.Tenant), expectedSessionRevision = retained.Snapshot.Revision });
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            model.Requests.Clear(); model.ReadEnabled = true;
            model.Arguments = System.Text.Json.JsonSerializer.Serialize(new { attachmentId = e.Id });
            var ready = (await sessions.FindTenantSessionAsync(e.Tenant, e.Session))!;
            var sent = await Json(await again.PostAsJsonAsync($"/api/v2/tenants/{e.Tenant}/sessions/{e.Session}/messages", new {
                messageId = Guid.NewGuid(), text = "Read that earlier file", expectedRevision = await Revision(again, e.Tenant), expectedSessionRevision = ready.Snapshot.Revision }));
            var run = await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), e.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            Assert.Equal(2, model.Requests.Count);
            var request = model.Requests.First();
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AttachmentsRead);
            var text = string.Join("\n", request.Messages.Select(m => m.Text));
            Assert.Contains("Files available in this session", text); Assert.Contains(format == "png" ? "read.png" : "read.txt", text);
            Assert.Contains(e.Id.ToString("D"), text); Assert.Contains("Keep this file available", text); Assert.Contains("Read that earlier file", text);
            var read = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AttachmentsRead);
            if (format == "png")
            {
                var image = Assert.Single(read.Parts!.OfType<ModelImageContent>());
                Assert.Equal("image/png", image.ContentType);
                using var decoded = SkiaSharp.SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, decoded.Width); Assert.Equal(2, decoded.Height);
            }
            else Assert.Contains("Private read body", read.Text);
            Assert.Equal(value.Bytes, await again.GetByteArrayAsync($"/api/v2/tenants/{e.Tenant}/sessions/{e.Session}/attachments/{e.Id}/content"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally
        {
            if (second is not null) await second.DisposeAsync(); if (first is not null) await first.DisposeAsync();
            SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
