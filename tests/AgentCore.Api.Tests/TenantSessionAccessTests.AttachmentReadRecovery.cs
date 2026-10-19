using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData("text", "allowed")] [InlineData("text", "revoke")] [InlineData("text", "source")]
    [InlineData("png", "allowed")] [InlineData("png", "revoke")] [InlineData("png", "source")]
    [InlineData("text", "capability")] [InlineData("png", "capability")]
    [InlineData("virtual-text", "allowed")] [InlineData("virtual-text", "revoke")] [InlineData("virtual-text", "source")]
    [InlineData("virtual-text", "capability")] [InlineData("virtual-text", "reader")]
    public async Task Tenant_attachment_model_read_checkpoint_rechecks_bytes_and_rehydrates_images_after_actual_SQLite_host_recreation(string format, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-read-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var virtualRead = format.StartsWith("virtual-", StringComparison.Ordinal);
        var readTool = virtualRead ? ToolCatalog.WorkspaceRead : ToolCatalog.AttachmentsRead;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); AttachmentReadCase value; Guid runId;
        try
        {
            var initial = new AttachmentReadModel { HoldAfterRead = true, ReadTool = readTool };
            await using (var first = new NativeTenantSqliteFactory(root, initial, s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))))
            {
                using var owner = first.CreateClient(); value = await PrepareAttachmentRead(first.Services, owner, virtualRead ? format[8..] : format, recoveryBudget: true, explicitCapability: boundary == "capability", virtualTool: virtualRead ? readTool : null); var v = value.Execution;
                var bound = await first.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(first.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, bound.RunId)).Status);
                initial.Requests.Clear(); initial.ReadEnabled = true; initial.Arguments = virtualRead ? System.Text.Json.JsonSerializer.Serialize(new { path = "/attachments/" + v.Id.ToString("D") }) : System.Text.Json.JsonSerializer.Serialize(new { attachmentId = v.Id });
                var session = (await first.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                    messageId = Guid.NewGuid(), text = "Read that earlier file", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
                runId = sent.GetProperty("runId").GetGuid(); await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                await initial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var run = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
                Assert.Equal(600, run.Admission.ExecutionBudget!.Profile.DurationSeconds); Assert.Equal(2, initial.Requests.Count);
                Assert.Null(Assert.Single(AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantAttachmentSources!).ProcessorVersion);
                Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var stored));
                Assert.Single(stored!, m => m.Role == ModelRole.Tool && m.Name == readTool);
                Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), run.Checkpoint!.PayloadJson);
            }
            clock.Advance(AgentRunCoordinator.ClaimDuration + TimeSpan.FromSeconds(2));
            var model = new AttachmentReadModel { ReadEnabled = true, ReadTool = readTool }; var enabled = false; var e = value.Execution;
            await using var second = new NativeTenantSqliteFactory(root, model, s => {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                s.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(
                    p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            });
            using var fresh = second.CreateClient(); var runs = second.Services.GetRequiredService<ITenantRunStore>();
            Assert.False((await runs.FindTenantRunAsync(e.Tenant, runId))!.IsTerminal); Assert.Empty(model.Requests);
            if (boundary == "revoke") await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(e.Human.PrincipalId, e.Tenant, e.Grant.AssignmentId,
                (await second.Services.GetRequiredService<IIamStore>().ReadTenantAsync(e.Tenant))!.Tenant.Revision);
            if (boundary == "source") await File.WriteAllTextAsync(Path.Combine(root, "attachments", "tenants", e.Tenant.ToString("N"), e.Session.ToString("N"), e.Id.ToString("N")), "changed retained source");
            if (boundary is "capability" or "reader")
            {
                await Login(fresh, "owner@example.test", "explicit-synthetic-test-password");
                var instance = (await Json(await fresh.GetAsync($"/api/v2/tenants/{e.Tenant}/agents/{value.Instance}"))).GetProperty("instance");
                var changed = await fresh.PatchAsJsonAsync($"/api/v2/tenants/{e.Tenant}/agents/{value.Instance}/settings/capabilities", new {
                    expectedRevision = await Revision(fresh, e.Tenant), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(),
                    set = new { selected = boundary == "reader" ? new[] { ToolCatalog.AttachmentsRead } : Array.Empty<string>() }, clear = Array.Empty<string>() });
                Assert.Equal(System.Net.HttpStatusCode.OK, changed.StatusCode);
            }
            Volatile.Write(ref enabled, true); await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            var completed = await AttachmentTerminal(runs, e.Tenant, runId);
            if (boundary == "allowed")
            {
                Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}; requests={model.Requests.Count}");
                var request = Assert.Single(model.Requests); var tool = request.Messages.Single(m => m.Role == ModelRole.Tool && m.Name == readTool);
                if (format == "png") { var image = Assert.Single(tool.Parts!.OfType<ModelImageContent>()); using var bitmap = SkiaSharp.SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, bitmap.Width); Assert.Equal(2, bitmap.Height); }
                else { Assert.Contains("Private read body", tool.Text); Assert.True(tool.Parts is null or { Count: 0 }); }
            }
            else { Assert.Equal(AgentRunStatus.Failed, completed.Status); Assert.Equal("authority-unavailable", completed.Failure!.Code); Assert.Empty(model.Requests); }
            Assert.Equal(0, second.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
