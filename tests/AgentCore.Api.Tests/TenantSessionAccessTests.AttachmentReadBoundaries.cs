using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static byte[] AttachmentTwoPagePdf()
    {
        var pdf = """
%PDF-1.1
1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 10 100 Td (Alpha page) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
7 0 obj<< /Length 43 >>stream
BT /F1 12 Tf 10 100 Td (Beta page) Tj ET
endstream
endobj
trailer<< /Root 1 0 R >>
%%EOF
""";
        return System.Text.Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n", StringComparison.Ordinal));
    }


    private sealed class HistoricalReadProcessor(IAttachmentProcessor inner) : IAttachmentProcessor
    {
        public string Version => inner.Version;
        public string? Mode { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(Guid session, IReadOnlyList<Guid> ids, CancellationToken ct = default) => inner.ProcessTurnAsync(session, ids, ct);
        public async ValueTask<AttachmentProcessResult> ProcessSourceAsync(AttachmentRecord source, byte[] bytes, CancellationToken ct = default)
        {
            if (Mode == "failure") throw new AgentCoreException("attachment_processing_failed", "Fixture processor cannot inspect the source.", 422);
            if (Mode == "hold")
            {
                Entered.TrySetResult(); await Release.Task;
                // A completed noncooperative processor must still be fenced by the owning Run.
                var result = await inner.ProcessSourceAsync(source, bytes, CancellationToken.None);
                Returned.TrySetResult(); return result;
            }
            return await inner.ProcessSourceAsync(source, bytes, ct);
        }
    }

    [Theory]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(false, "other-session")] [InlineData(true, "other-session")]
    [InlineData(false, "processor")] [InlineData(true, "processor")]
    [InlineData(false, "window")] [InlineData(true, "window")]
    public async Task Tenant_historical_attachment_read_refuses_exact_unavailable_source_or_window_without_image_parts(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-historical-read-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        HistoricalReadProcessor? processor = null;
        try
        {
            var model = new AttachmentReadModel();
            void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IAttachmentProcessor>(p => processor = new(new AttachmentProcessor(p.GetRequiredService<IAttachmentStore>()))));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, boundary == "window" ? "text" : "png"); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var admitted = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, admitted.RunId)).Status);
            var target = v.Session;
            if (boundary == "other-session") target = (await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions", new {
                agentInstanceId = value.Instance, expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1, modelKey = "scripted-vision" }))).GetProperty("sessionId").GetGuid();
            if (boundary == "other-session")
            {
                using var body = new MultipartFormDataContent();
                var local = new ByteArrayContent(value.Bytes); local.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                body.Add(local, "file", "local.png");
                var localId = (await Json(await owner.PostAsync($"/api/v2/tenants/{v.Tenant}/sessions/{target}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, v.Tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
                var localTurn = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, target, v.Human, Guid.NewGuid(), "Keep only this local image", [localId], await Revision(owner, v.Tenant), 1);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, localTurn.RunId)).Status);
            }
            model.Requests.Clear(); model.ReadEnabled = true;
            model.Arguments = boundary == "window" ? JsonSerializer.Serialize(new { attachmentId = v.Id, range = "A1" })
                : JsonSerializer.Serialize(new { attachmentId = boundary == "unknown" ? Guid.NewGuid() : v.Id });
            if (boundary == "processor") processor!.Mode = "failure";
            var current = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, target))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{target}/messages", new {
                messageId = Guid.NewGuid(), text = "Read the exact earlier source", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = current.Snapshot.Revision }));
            var done = await AttachmentTerminal(runs, v.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.Equal(2, model.Requests.Count);
            var result = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AttachmentsRead);
            using var json = JsonDocument.Parse(result.Text);
            Assert.Equal(boundary == "processor" ? "attachment_read_unavailable" : boundary == "window" ? "invalid" : "forbidden", json.RootElement.GetProperty("error").GetString());
            Assert.Null(result.Parts); Assert.DoesNotContain("Private read body", result.Text); Assert.DoesNotContain("read.png", result.Text);
            Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), result.Text);
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/attachments/{v.Id}/content"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { processor?.Release.TrySetResult(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
