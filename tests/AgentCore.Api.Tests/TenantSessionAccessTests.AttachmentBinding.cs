using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "missing")] [InlineData(true, "missing")] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "parent")] [InlineData(true, "parent")] [InlineData(false, "review")] [InlineData(true, "review")]
    public async Task Tenant_attachment_context_uses_only_independent_current_executor_reads_and_preserves_original_binding(bool sqlite, string mode)
    {
        var allowed = mode == "allowed"; Guid? agentAssignment = null;
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-binding-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel(); await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var path = api + $"/sessions/{session}/attachments"; using var body = new MultipartFormDataContent(); var file = new ByteArrayContent("Private attachment body"u8.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); body.Add(file, "file", "Private file label.txt");
            var id = (await Json(await owner.PostAsync(path + $"?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single();
            if (mode != "missing")
            {
                var access = factory.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human.PrincipalId, tenant, null, "Agent attachment reads", mode == "parent" ? ["attachments.read"] : ["sessions.read", "attachments.read"], await Revision(owner, tenant));
                agentAssignment = (await access.AssignRoleAsync(human.PrincipalId, tenant, agent.GetProperty("principalId").GetGuid(), role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant))).AssignmentId;
                if (mode == "review") await factory.Services.GetRequiredService<TenantOperationPolicyService>().SaveAsync(tenant,
                    new(human.AccountId, login.Session.SessionId, human.PrincipalId), "attachments.read", new(OperationApprovalMode.Always, false), await Revision(owner, tenant), 0);
                state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            }
            var receipt = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(tenant, session,
                new(human.AccountId, login.Session.SessionId, human.PrincipalId), Guid.NewGuid(), "Read this", [id], state.Tenant.Revision, 1);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? run = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                run = await runs.FindTenantRunAsync(tenant, receipt.RunId, timeout.Token); if (run?.IsTerminal == true) break;
                await Task.Delay(50, timeout.Token);
            }
            Assert.NotNull(run); Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            Assert.NotEmpty(model.Requests);
            var prompt = string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text + "\n" + string.Join("\n", (m.Parts ?? []).OfType<ModelTextContent>().Select(p => p.Text))));
            if (allowed) { Assert.Contains("Private attachment body", prompt); Assert.Contains("Private file label.txt", prompt); }
            else { Assert.DoesNotContain("Private attachment body", prompt); Assert.DoesNotContain("Private file label.txt", prompt); Assert.Contains(id.ToString("D"), prompt); Assert.Contains("User-provided attachment references", prompt); Assert.Contains("unavailable under current access", prompt); }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            Assert.Equal(AttachmentState.Bound, (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(tenant, session, id))!.State);
            if (allowed)
            {
                var priorRequests = model.Requests.Count;
                var currentSession = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var followup = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new {
                    messageId = Guid.NewGuid(), text = "Which files are available?", expectedRevision = await Revision(owner, tenant),
                    expectedSessionRevision = currentSession.Snapshot.Revision }));
                var later = await AttachmentTerminal(runs, tenant, followup.GetProperty("runId").GetGuid());
                Assert.Equal(AgentRunStatus.Completed, later.Status);
                var laterPrompt = string.Join("\n", model.Requests.Skip(priorRequests).SelectMany(r => r.Messages).Select(m => m.Text));
                Assert.Contains("Private file label.txt", laterPrompt); Assert.Contains(id.ToString("D"), laterPrompt);
                Assert.DoesNotContain("Private attachment body", laterPrompt); // Historical input is metadata; rereading is a separate operation.
                await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human.PrincipalId, tenant, agentAssignment!.Value, await Revision(owner, tenant));
                var afterReads = model.Requests.Count; currentSession = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var hidden = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new {
                    messageId = Guid.NewGuid(), text = "Continue without new files", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = currentSession.Snapshot.Revision }));
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, tenant, hidden.GetProperty("runId").GetGuid())).Status);
                var hiddenPrompt = string.Join("\n", model.Requests.Skip(afterReads).SelectMany(r => r.Messages).Select(m => m.Text));
                Assert.DoesNotContain(id.ToString("D"), hiddenPrompt); Assert.DoesNotContain("Private file label.txt", hiddenPrompt); Assert.DoesNotContain("Private attachment body", hiddenPrompt);
                Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
