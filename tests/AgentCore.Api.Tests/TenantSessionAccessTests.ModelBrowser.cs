using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory] [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_Browser_model_requests_exact_Review_and_resumes_once_with_durable_bounded_observation(bool sqlite, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (baseFactory is ApprovalExecutionFactory memory) model = memory.Model;
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Reviewed browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read browser Session", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var url = native.Fixture.Origin + "/browser-native.html";
            model.ProposedBrowserCommand = JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate, arguments = new { url } } });
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Navigate to the exact approved local Browser page", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid(); if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                while (!deadline.IsCancellationRequested)
                {
                    waiting = await runs.FindTenantRunAsync(tenant, runId);
                    if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId);
            Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts); // Actual model preparation registered only metadata.
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.NotNull(request.Operation.BrowserCommand); Assert.Null(waiting.Claim);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"), new { decision,
                actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            AgentRun? completed = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await CurrentApprovalExecution(runs, tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            Assert.NotEqual(runId, completed.AgentRunId);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var transcript));
            Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(transcript!));
            var result = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var body = JsonDocument.Parse(result.Text); var output = body.RootElement;
            Assert.Equal(decision == "approve", output.GetProperty("commandCompleted").GetBoolean()); Assert.False(output.GetProperty("applicationOutcomeVerified").GetBoolean());
            Assert.False(output.GetProperty("replayAllowed").GetBoolean()); Assert.Equal(JsonValueKind.Null, output.GetProperty("applied").ValueKind);
            Assert.Equal(decision == "approve" ? 1 : 0, (await native.ObserveTenantSessionAsync(scope)).OpenPages);
            if (decision == "approve")
            {
                Assert.False(output.GetProperty("observationUnavailable").GetBoolean()); Assert.Contains("untrustedBrowserContent", result.Text); Assert.Contains(url, result.Text);
                Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result.Text));
                Assert.Equal(TenantOperationOutcome.Completed, (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!.Outcome);
            }
            Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "approvals.request");
            Assert.Equal(decision == "approve" ? 1 : 0, (await native.PrepareTenantCommandAsync(scope, new BrowserGetConfig())).Witness!.CommandRevision);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
