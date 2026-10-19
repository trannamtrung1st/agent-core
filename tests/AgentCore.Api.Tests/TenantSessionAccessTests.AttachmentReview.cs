using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task<TenantApprovalRequest> AwaitAttachmentReview(IServiceProvider services, Guid tenant, Guid source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var approvals = services.GetRequiredService<ITenantApprovalStore>();
        while (true)
        {
            var page = await approvals.ListApprovalsAsync(tenant, null, null, new(0, 100), timeout.Token);
            if (page.Items.SingleOrDefault(r => r.RunId == source) is { } found) return found;
            var run = (await services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, source, timeout.Token))!;
            if (run.IsTerminal) throw new InvalidOperationException($"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            await Task.Delay(50, timeout.Token);
        }
    }
    [Theory] [InlineData(false, "text", "approve")] [InlineData(true, "text", "approve")]
    [InlineData(false, "png", "approve")] [InlineData(true, "png", "approve")]
    [InlineData(false, "docx", "approve")] [InlineData(true, "docx", "approve")]
    [InlineData(false, "xlsx-range", "approve")] [InlineData(true, "xlsx-range", "approve")]
    [InlineData(false, "text", "reject")] [InlineData(true, "text", "reject")]
    [InlineData(false, "text", "cancel")] [InlineData(true, "text", "cancel")]
    public async Task Tenant_attachment_model_exact_Review_HTTP_decision_returns_only_saved_window_through_actual_successor(bool sqlite, string format, string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && format == "text" && choice == "approve" && Environment.GetEnvironmentVariable("AGENTCORE_ATTACHMENT_REVIEW_UI_PROOF") == "1";
        const string proofPath = "/private/tmp/agent-core-attachment-review-ui";
        try
        {
            var model = new AttachmentReadModel { ReviewRead = true }; var enabled = true;
            void Configure(IServiceCollection s)
            {
                if (proof)
                {
                    s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ArtifactProofClock()));
                    ((IdentityHttpOptions)s.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5221"];
                }
                s.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(
                    p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, format,
                displayNameOverride: proof ? "Quarterly-attachment-with-long-international-試験-thử-nghiệm-2026-quarterly-summary.txt" : null); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}"; var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var bound = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this file available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, bound.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true;
            model.Arguments = format == "docx" ? JsonSerializer.Serialize(new { attachmentId = v.Id, offset = 1, limit = 1 })
                : format == "xlsx-range" ? JsonSerializer.Serialize(new { attachmentId = v.Id, sheet = "Data", range = "A1:B1" }) : JsonSerializer.Serialize(new { attachmentId = v.Id });
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/attachments.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            async Task<Guid> Submit()
            {
                var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                return (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new {
                    messageId = Guid.NewGuid(), text = $"Read the exact earlier Attachment {v.Id:D}", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }))).GetProperty("runId").GetGuid();
            }
            var source = await Submit(); var request = await AwaitAttachmentReview(factory.Services, v.Tenant, source);
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(v.Id, request.Operation.AttachmentRead!.AttachmentId); Assert.Single(model.Requests);
            Assert.DoesNotContain("Private read body", string.Join("\n", model.Requests.First().Messages.Select(m => m.Text))); Assert.DoesNotContain(request.Operation.AttachmentRead.DisplayName, string.Join("\n", model.Requests.First().Messages.Select(m => m.Text)));
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("read_attachment", detail.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "attachmentDisplayName");
            Volatile.Write(ref enabled, false);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant = v.Tenant, session = v.Session, requestId = request.RequestId, displayName = request.Operation.AttachmentRead.DisplayName }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while ((await approvals.FindApprovalAsync(v.Tenant, request.RequestId, deadline.Token))!.Status == TenantApprovalStatus.Pending) await Task.Delay(100, deadline.Token);
            }
            else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (choice == "cancel" ? "cancel" : "decision"), new {
                decision = choice, actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            Volatile.Write(ref enabled, true); var done = await AwaitBrowserRestartRun(runs, v.Tenant, source, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.NotEqual(source, done.AgentRunId);
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var doc = JsonDocument.Parse(result.Text); Assert.Equal(choice == "approve", doc.RootElement.GetProperty("readAuthorized").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("applied").GetBoolean()); Assert.Null(await approvals.FindOperationGrantAsync(v.Tenant, request.RequestId));
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(v.Tenant, request.Operation.OperationId);
            if (choice == "approve")
            {
                Assert.Equal(TenantOperationOutcome.Completed, receipt!.Outcome); var data = doc.RootElement.GetProperty("attachmentObservation"); Assert.True(data.GetProperty("tenantAttachmentRead").GetBoolean());
                if (format == "png") { var image = Assert.Single(result.Parts!.OfType<ModelImageContent>()); using var bitmap = SkiaSharp.SKBitmap.Decode(image.Bytes.ToArray()); Assert.Equal(2, bitmap.Width); }
                else { Assert.Contains("Private read body", result.Text); Assert.True(result.Parts is null or { Count: 0 }); }
                Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), done.Checkpoint!.PayloadJson);
            }
            else { Assert.Null(receipt); Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("attachmentObservation").ValueKind); Assert.True(result.Parts is null or { Count: 0 }); }
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync(api + $"/sessions/{v.Session}/attachments/{v.Id}/content")); Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{\"completed\":true}");
                model.Requests.Clear(); var staleSource = await Submit(); var stale = await AwaitAttachmentReview(factory.Services, v.Tenant, staleSource);
                await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId, (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!.Tenant.Revision);
                await File.WriteAllTextAsync(proofPath + "-stale.json", JsonSerializer.Serialize(new { tenant = v.Tenant, session = v.Session, requestId = stale.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while ((await approvals.FindApprovalAsync(v.Tenant, stale.RequestId, deadline.Token))!.Status == TenantApprovalStatus.Pending) await Task.Delay(100, deadline.Token);
                Assert.Equal(TenantApprovalStatus.Cancelled, (await approvals.FindApprovalAsync(v.Tenant, stale.RequestId))!.Status);
                _ = await AwaitBrowserRestartRun(runs, v.Tenant, staleSource, false);
                await File.WriteAllTextAsync(proofPath + "-stale-completed.json", "{\"completed\":true}");
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
