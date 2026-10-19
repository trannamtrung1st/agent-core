using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Domain.Security;
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
    [InlineData("text", "pending")] [InlineData("png", "pending")]
    public async Task Tenant_attachment_Review_checkpoint_rechecks_consent_bytes_and_images_after_actual_SQLite_host_recreation(string format, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-review-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); AttachmentReadCase value; Guid runId, sourceId; TenantApprovalRequest consent;
        try
        {
            var initial = new AttachmentReadModel { ReviewRead = true, HoldAfterRead = true };
            await using (var first = new NativeTenantSqliteFactory(root, initial, s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))))
            {
                using var owner = first.CreateClient(); value = await PrepareAttachmentRead(first.Services, owner, format, recoveryBudget: true, explicitCapability: boundary == "capability"); var v = value.Execution;
                var bound = await first.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(first.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, bound.RunId)).Status);
                Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{v.Tenant}/operation-policies/attachments.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
                initial.Requests.Clear(); initial.ReadEnabled = true; initial.Arguments = System.Text.Json.JsonSerializer.Serialize(new { attachmentId = v.Id });
                var session = (await first.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                    messageId = Guid.NewGuid(), text = "Read that earlier file", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
                sourceId = sent.GetProperty("runId").GetGuid();
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                consent = await AwaitAttachmentReview(first.Services, v.Tenant, sourceId);
                Assert.Single(initial.Requests); runId = sourceId;
                if (boundary != "pending")
                {
                    var detail = await Json(await owner.GetAsync($"/api/v2/tenants/{v.Tenant}/approvals/{consent.RequestId}"));
                    Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/approvals/{consent.RequestId}/decision", new {
                        decision = "approve", actionHash = consent.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = consent.Revision })).StatusCode);
                    await first.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                    await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                    await initial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var source = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, sourceId))!;
                    runId = source.Result!.ContinuationRunId!.Value;
                    var run = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
                    Assert.Equal(600, run.Admission.ExecutionBudget!.Profile.DurationSeconds); Assert.Equal(2, initial.Requests.Count);
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var stored));
                    Assert.Single(stored!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                    Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), run.Checkpoint!.PayloadJson);
                }
            }
            clock.Advance(AgentRunCoordinator.ClaimDuration + TimeSpan.FromSeconds(2));
            var model = new AttachmentReadModel { ReviewRead = true, ReadEnabled = true }; var enabled = false; var e = value.Execution;
            await using var second = new NativeTenantSqliteFactory(root, model, s => {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                s.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(
                    p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            });
            using var fresh = second.CreateClient(); var runs = second.Services.GetRequiredService<ITenantRunStore>();
            Assert.False((await runs.FindTenantRunAsync(e.Tenant, runId))!.IsTerminal); Assert.Empty(model.Requests);
            if (boundary == "pending")
            {
                await Login(fresh, "owner@example.test", "explicit-synthetic-test-password");
                var detail = await Json(await fresh.GetAsync($"/api/v2/tenants/{e.Tenant}/approvals/{consent.RequestId}"));
                Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                Assert.Equal(System.Net.HttpStatusCode.OK, (await fresh.PostAsJsonAsync($"/api/v2/tenants/{e.Tenant}/approvals/{consent.RequestId}/decision", new {
                    decision = "approve", actionHash = consent.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = consent.Revision })).StatusCode);
            }
            if (boundary == "revoke") await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(e.Human.PrincipalId, e.Tenant, e.Grant.AssignmentId,
                (await second.Services.GetRequiredService<IIamStore>().ReadTenantAsync(e.Tenant))!.Tenant.Revision);
            if (boundary == "source") await File.WriteAllTextAsync(Path.Combine(root, "attachments", "tenants", e.Tenant.ToString("N"), e.Session.ToString("N"), e.Id.ToString("N")), "changed retained source");
            if (boundary == "capability")
            {
                await Login(fresh, "owner@example.test", "explicit-synthetic-test-password");
                var instance = (await Json(await fresh.GetAsync($"/api/v2/tenants/{e.Tenant}/agents/{value.Instance}"))).GetProperty("instance");
                var changed = await fresh.PatchAsJsonAsync($"/api/v2/tenants/{e.Tenant}/agents/{value.Instance}/settings/capabilities", new {
                    expectedRevision = await Revision(fresh, e.Tenant), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(),
                    set = new { selected = Array.Empty<string>() }, clear = Array.Empty<string>() });
                Assert.Equal(System.Net.HttpStatusCode.OK, changed.StatusCode);
            }
            await second.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            Volatile.Write(ref enabled, true); await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            var completed = await AwaitBrowserRestartRun(runs, e.Tenant, sourceId, false);
            if (boundary is "allowed" or "pending")
            {
                Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}; requests={model.Requests.Count}");
                var request = Assert.Single(model.Requests); var tool = request.Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                if (format == "png") { var image = Assert.Single(tool.Parts!.OfType<ModelImageContent>()); using var bitmap = SkiaSharp.SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, bitmap.Width); Assert.Equal(2, bitmap.Height); }
                else { Assert.Contains("Private read body", tool.Text); Assert.True(tool.Parts is null or { Count: 0 }); }
            }
            else { Assert.Equal(AgentRunStatus.Failed, completed.Status); Assert.Equal("authority-unavailable", completed.Failure!.Code); Assert.Empty(model.Requests); }
            Assert.Null(await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(e.Tenant, consent.RequestId));
            Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(e.Tenant, 200), a => a.Action == TenantAttachmentReadRules.AuditAction);
            Assert.Equal(0, second.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
