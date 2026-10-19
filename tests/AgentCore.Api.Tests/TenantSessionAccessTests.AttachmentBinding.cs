using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_internal_attachment_binding_cannot_reach_unconverted_model_context_or_legacy_processing(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-binding-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var path = api + $"/sessions/{session}/attachments"; using var body = new MultipartFormDataContent(); var file = new ByteArrayContent("Private attachment body"u8.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); body.Add(file, "file", "Private file label.txt");
            var id = (await Json(await owner.PostAsync(path + $"?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single();
            var receipt = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(tenant, session,
                new(human.AccountId, login.Session.SessionId, human.PrincipalId), Guid.NewGuid(), "Read this", [id], state.Tenant.Revision, 1);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? run = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                run = await runs.FindTenantRunAsync(tenant, receipt.RunId, timeout.Token); if (run?.IsTerminal == true) break;
                await Task.Delay(50, timeout.Token);
            }
            Assert.NotNull(run); Assert.Equal(AgentRunStatus.Failed, run.Status); Assert.Equal("attachment-runtime-unavailable", run.Failure!.Code);
            Assert.Empty(model.Requests); Assert.Null(run.Checkpoint); Assert.Null(run.Result);
            Assert.Equal(AttachmentState.Bound, (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(tenant, session, id))!.State);
            Assert.DoesNotContain("Private file", run.Failure.Summary);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
