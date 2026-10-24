using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class MixedHistoricalReadModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public bool ReadEnabled { get; set; }
        public Guid Image { get; set; }
        public ModelCapabilities Capabilities => inner.Capabilities with { Vision = true, Tools = true };
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (ReadEnabled && !request.Messages.Any(m => m.Role == ModelRole.Tool))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AttachmentsRead);
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkspaceList);
                yield return new ModelToolCallEvent(new("image-read", ToolCatalog.AttachmentsRead, JsonSerializer.Serialize(new { attachmentId = Image })));
                yield return new ModelToolCallEvent(new("metadata-read", ToolCatalog.WorkspaceList, """{"path":"/attachments"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Synthetic_historical_image_marker_reads_actual_owned_source_and_keeps_ephemeral_parts_out_of_history(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-historical-image-synthetic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "png"); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var first = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this image available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, first.RunId)).Status);
            model.Requests.Clear();
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                messageId = Guid.NewGuid(), text = ScriptedLanguageModel.HistoricalImageRereadMarker + " Please inspect the earlier image.", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }));
            var done = await AttachmentTerminal(runs, v.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.Equal(2, model.Requests.Count);
            var read = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AttachmentsRead);
            var image = Assert.Single(read.Parts!.OfType<ModelImageContent>()); Assert.Equal("image/png", image.ContentType);
            using var decoded = SkiaSharp.SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, decoded.Width); Assert.Equal(2, decoded.Height);
            var history = (await Json(await owner.GetAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages"))).GetProperty("items");
            Assert.Contains(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString()!.Contains(ScriptedLanguageModel.HistoricalImageRereadAnswer, StringComparison.Ordinal));
            var retained = history.GetRawText();
            Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), retained); Assert.DoesNotContain("Treat this as tool data", retained, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), done.Checkpoint!.PayloadJson);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_mixed_historical_reads_attach_image_parts_only_to_the_exact_image_tool_result(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-historical-image-mixed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new MixedHistoricalReadModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "png", virtualTool: ToolCatalog.WorkspaceList); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var first = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this image available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, first.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true; model.Image = v.Id;
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                messageId = Guid.NewGuid(), text = "Read the exact historical image and list its metadata", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }));
            var done = await AttachmentTerminal(runs, v.Tenant, sent.GetProperty("runId").GetGuid());
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.Equal(2, model.Requests.Count);
            var messages = model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); Assert.Equal(2, messages.Length);
            var image = Assert.Single(messages, m => m.Name == ToolCatalog.AttachmentsRead); Assert.Equal("image-read", image.ToolCallId);
            Assert.Single(image.Parts!.OfType<ModelImageContent>()); Assert.Contains(v.Id.ToString("D"), image.Text);
            var metadata = Assert.Single(messages, m => m.Name == ToolCatalog.WorkspaceList); Assert.Equal("metadata-read", metadata.ToolCallId);
            Assert.True(metadata.Parts is null or { Count: 0 }); Assert.Contains("read.png", metadata.Text);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_stop_or_shutdown_during_noncooperative_historical_image_read_never_reenters_model_with_stale_parts(bool sqlite, bool shutdown)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-historical-image-stale-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        HistoricalReadProcessor? processor = null;
        try
        {
            var model = new AttachmentReadModel();
            void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IAttachmentProcessor>(p => processor = new(new AttachmentProcessor(p.GetRequiredService<IAttachmentStore>()))));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            try
            {
                using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "png"); var v = value.Execution;
                var runs = factory.Services.GetRequiredService<ITenantRunStore>();
                var first = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this image available", [v.Id], await Revision(owner, v.Tenant), 1);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, first.RunId)).Status);
                model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = JsonSerializer.Serialize(new { attachmentId = v.Id }); processor!.Mode = "hold";
                var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                    messageId = Guid.NewGuid(), text = "Read the exact historical image", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }));
                var id = sent.GetProperty("runId").GetGuid(); await processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var running = (await runs.FindTenantRunAsync(v.Tenant, id))!;
                var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
                if (shutdown) await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/agent-runs/{id}/cancel", new {
                    expectedRevision = await Revision(owner, v.Tenant), expectedRunRevision = running.Revision })).StatusCode);
                processor.Release.TrySetResult(); await processor.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.WaitForGenerationReleaseAsync(v.Session, id, running.Claim!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                var done = (await runs.FindTenantRunAsync(v.Tenant, id))!;
                Assert.NotEqual(AgentRunStatus.Completed, done.Status); if (!shutdown) Assert.Equal(AgentRunStatus.Cancelled, done.Status);
                Assert.Single(model.Requests); Assert.DoesNotContain(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Parts is { Count: > 0 }));
                var history = (await Json(await owner.GetAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages"))).GetProperty("items").GetRawText();
                Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), history); Assert.DoesNotContain(ScriptedLanguageModel.HistoricalImageRereadAnswer, history);
                Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
            finally { processor?.Release.TrySetResult(); }
        }
        finally { processor?.Release.TrySetResult(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
