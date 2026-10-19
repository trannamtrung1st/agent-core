using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task AttachmentComposerProof()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-composer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string proof = "/private/tmp/agent-core-attachment-message-ui";
        const string mainName = "agent-core-provided-file-long-international-試験-thử-nghiệm-2026-report.txt";
        const string retryName = "agent-core-provided-file-retry.txt";
        try
        {
            var model = new AttachmentReadModel { ReadEnabled = true, ReviewRead = true, ReadSubmittedSelector = true };
            await using var factory = new NativeTenantMemoryFactory(root, model, s => {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ArtifactProofClock()));
                ((IdentityHttpOptions)s.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5221"];
            });
            factory.UseKestrel(0); using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text"); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/attachments/{v.Id}?requestId={Guid.NewGuid()}&expectedRevision={await Revision(owner, v.Tenant)}&expectedSessionRevision=1")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{v.Tenant}/operation-policies/attachments.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, v.Tenant), expectedPolicyRevision = 0 })).StatusCode);
            await File.WriteAllTextAsync("/private/tmp/" + mainName, "Private read body → thử nghiệm");
            await File.WriteAllTextAsync("/private/tmp/agent-core-provided-file-remove.txt", "Remove this pending file");
            await File.WriteAllTextAsync("/private/tmp/" + retryName, "Explicit retry after a lost upload acknowledgement");
            await File.WriteAllTextAsync(proof + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant = v.Tenant, session = v.Session, mainFile = "/private/tmp/" + mainName, removeFile = "/private/tmp/agent-core-provided-file-remove.txt", retryFile = "/private/tmp/" + retryName }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30)); var ct = deadline.Token;
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); TenantApprovalRequest? request = null;
            while (request is null)
            {
                request = (await approvals.ListApprovalsAsync(v.Tenant, null, null, new(0, 100), ct)).Items.SingleOrDefault(r => r.SessionId == v.Session && r.Operation.AttachmentRead is not null);
                if (request is null) await Task.Delay(100, ct);
            }
            Assert.Equal(mainName, request.Operation.AttachmentRead!.DisplayName);
            var initial = Assert.Single(model.Requests); var initialText = string.Join("\n", initial.Messages.Select(m => m.Text));
            Assert.Contains(request.Operation.AttachmentRead.AttachmentId.ToString("D"), initialText); Assert.DoesNotContain("agent-core-provided-file-long-international", initialText); Assert.DoesNotContain("Private read body", initialText);
            await File.WriteAllTextAsync(proof + "-pending.json", JsonSerializer.Serialize(new { requestId = request.RequestId, runId = request.RunId }));
            while ((await approvals.FindApprovalAsync(v.Tenant, request.RequestId, ct))!.Status == TenantApprovalStatus.Pending) await Task.Delay(100, ct);
            Assert.Equal(TenantApprovalStatus.Approved, (await approvals.FindApprovalAsync(v.Tenant, request.RequestId, ct))!.Status);
            var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, request.RunId, false);
            Assert.Equal(AgentRunStatus.Completed, done.Status);
            var result = model.Requests.Last().Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var data = JsonDocument.Parse(result.Text);
            Assert.Equal("Private read body → thử nghiệm", data.RootElement.GetProperty("attachmentObservation").GetProperty("inspection").GetProperty("content").GetString()); Assert.Null(await approvals.FindOperationGrantAsync(v.Tenant, request.RequestId));
            Assert.Equal("Private read body → thử nghiệm", Encoding.UTF8.GetString(await owner.GetByteArrayAsync(api + $"/attachments/{request.Operation.AttachmentRead.AttachmentId}/content")));
            await File.WriteAllTextAsync(proof + "-completed.json", "{\"completed\":true}");
            var files = factory.Services.GetRequiredService<ITenantAttachmentStore>(); AttachmentRecord? uncertain = null;
            while (uncertain is null)
            {
                uncertain = (await files.ListTenantAsync(v.Tenant, v.Session, null, null, 100, ct)).Items.SingleOrDefault(a => a.DisplayName == retryName && a.State == AttachmentState.Pending);
                if (uncertain is null) await Task.Delay(100, ct);
            }
            await File.WriteAllTextAsync(proof + "-uncertain.json", JsonSerializer.Serialize(new { attachmentId = uncertain.AttachmentId, uploadId = uncertain.UploadRequestId }));
            while (!File.Exists(proof + "-retry-ready")) await Task.Delay(100, ct);
            Assert.Equal(uncertain, await files.FindTenantAsync(v.Tenant, v.Session, uncertain.AttachmentId, ct));
            Assert.Single((await files.ListTenantAsync(v.Tenant, v.Session, null, null, 100, ct)).Items, a => a.UploadRequestId == uncertain.UploadRequestId);
            await File.WriteAllTextAsync(proof + "-retry-verified.json", "{\"completed\":true}");
            while (await files.FindTenantAsync(v.Tenant, v.Session, uncertain.AttachmentId, ct) is not null) await Task.Delay(100, ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/attachments/{uncertain.AttachmentId}/content", ct)).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            await File.WriteAllTextAsync(proof + "-recovery-completed.json", "{\"completed\":true}");
            while (!File.Exists(proof + "-release")) await Task.Delay(100, ct);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
