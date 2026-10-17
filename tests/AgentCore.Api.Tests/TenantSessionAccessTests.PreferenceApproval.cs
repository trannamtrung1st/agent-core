using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false, "approve", false)] [InlineData(true, false, "approve", false)]
    [InlineData(false, true, "approve", false)] [InlineData(true, true, "approve", false)]
    [InlineData(false, false, "reject", false)] [InlineData(true, false, "reject", false)]
    [InlineData(false, true, "reject", false)] [InlineData(true, true, "reject", false)]
    [InlineData(false, false, "cancel", false)] [InlineData(true, false, "cancel", false)]
    [InlineData(false, false, "approve", true)] [InlineData(true, false, "approve", true)]
    public async Task Model_preference_authorization_persists_exact_parameters_and_resumes_the_saved_effect(bool sqlite, bool review, string decision, bool freeze)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-preference-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (factory is ApprovalExecutionFactory memory) model = memory.Model;
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }
            })).StatusCode);
            var agents = factory.Services.GetRequiredService<ITenantAgentStore>(); var before = (await agents.FindTenantAgentAsync(tenant, instance))!;
            if (review)
            {
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var actor = Assert.Single(state.Users).PrincipalId;
                var access = factory.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(actor, tenant, null, "Own preferences", ["agents.read", "agents.update"], await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, before.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            }
            model.ProposedPreferenceAuthorization = """{"set":{"language":"vi","responseLength":"concise"},"clear":[]}""";
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Request authorization for concise Vietnamese responses", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memoryExecution) Volatile.Write(ref memoryExecution.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    waiting = await runs.FindTenantRunAsync(tenant, runId);
                    if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Summary}");
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var request = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
            Assert.Null(request.Operation.Persona); Assert.Equal("vi", request.Operation.Preferences!.Language);
            Assert.Equal(2, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
            var projected = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("update_preferences", projected.GetProperty("preparedAction").GetProperty("operation").GetString());
            Assert.Contains("conversation preferences", projected.GetProperty("summary").GetString());
            if (freeze)
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/self-management/freeze", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2
                })).StatusCode);
            var decided = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"), new {
                decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision
            });
            if (freeze)
            {
                Assert.Equal(HttpStatusCode.Conflict, decided.StatusCode);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
                Assert.Equal(before.Instance.SettingsOverrides, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.SettingsOverrides);
                Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Status);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            AgentRun? completed = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await runs.FindTenantRunAsync(tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var after = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(decision == "approve" || freeze ? 3 : 2, after.Revision); Assert.Equal(before.Instance.Persona, after.Persona);
            if (decision == "approve" && !freeze) { Assert.Equal("vi", after.SettingsOverrides!.Language!.Value); Assert.Equal("concise", after.SettingsOverrides.ResponseLength!.Value); }
            else Assert.Equal(before.Instance.SettingsOverrides, after.SettingsOverrides);
            var grant = await approvals.FindOperationGrantAsync(tenant, request.RequestId);
            if (!review && decision == "approve") Assert.Equal(freeze ? OperationGrantStatus.Revoked : OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
            Assert.Contains(model.Requests, r => r.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true);
            Assert.True(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            Assert.DoesNotContain(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.PendingCalls(messages!), c => c.Name == ToolCatalog.RequestAuthorization);
            Assert.Equal(decision == "approve" ? 1 : 0, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.preferences.apply"));
            Assert.Null(completed.Owner.ProfileIdOrNull);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
