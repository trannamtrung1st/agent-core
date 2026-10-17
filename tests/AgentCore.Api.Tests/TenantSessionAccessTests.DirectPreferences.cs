using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task Model_managed_preferences_use_current_authority_and_direct_receipts_through_worker_and_Runtime(bool sqlite, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-direct-preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (factory is ApprovalExecutionFactory memory) model = memory.Model;
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new
            {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Settings" } }, autoApply = new { value = new[] { "ConversationPreferences" } } }
            })).StatusCode);
            var agents = factory.Services.GetRequiredService<ITenantAgentStore>(); var before = (await agents.FindTenantAgentAsync(tenant, instance))!;
            var iam = factory.Services.GetRequiredService<IIamStore>(); var state = (await iam.ReadTenantAsync(tenant))!;
            var actor = Assert.Single(state.Users).PrincipalId; var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(actor, tenant, null, "Own preferences", allowed ? ["agents.read", "agents.update"] : ["agents.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(actor, tenant, before.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            model.ProposedPreferences = """{"set":{"language":"vi","responseLength":"concise"},"clear":[]}""";
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Use concise Vietnamese responses", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memoryExecution) Volatile.Write(ref memoryExecution.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? completed = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await runs.FindTenantRunAsync(tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var outcome = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == TenantPreferenceToolContract.Name);
            using var result = System.Text.Json.JsonDocument.Parse(outcome.Text);
            Assert.Equal(allowed, result.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(allowed ? "allowed" : "requiresApproval", result.RootElement.GetProperty("decision").GetString());
            var current = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(allowed ? 3 : 2, current.Revision); Assert.Equal(before.Instance.Persona, current.Persona);
            if (allowed) { Assert.Equal("vi", current.SettingsOverrides!.Language!.Value); Assert.Equal("concise", current.SettingsOverrides.ResponseLength!.Value); }
            else Assert.Equal(before.Instance.SettingsOverrides, current.SettingsOverrides);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            var receipt = await factory.Services.GetRequiredService<ITenantPreferenceOperationStore>().FindPreferenceReceiptAsync(tenant, runId, "direct-preferences");
            if (allowed) { Assert.NotNull(receipt); Assert.Null(receipt.RequestId); } else Assert.Null(receipt);
            Assert.Null(completed.Owner.ProfileIdOrNull);
            Assert.Equal(allowed ? 1 : 0, (await iam.ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.preferences.apply"));
            Assert.Contains(model.Requests, r => r.Tools?.Any(t => t.Name == TenantPreferenceToolContract.Name) == true);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
