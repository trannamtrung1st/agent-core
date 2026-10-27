using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
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
    [TenantBrowserChromiumTheory] [InlineData(false, false, false, "browser.get_config")] [InlineData(true, false, false, "browser.get_config")]
    [InlineData(false, true, false, "browser.snapshot")] [InlineData(true, true, false, "browser.snapshot")]
    [InlineData(false, false, true, "browser.get_config")] [InlineData(true, false, true, "browser.get_config")]
    [InlineData(false, true, false, "browser.tabs")] [InlineData(true, true, false, "browser.tabs")]
    [InlineData(false, true, false, "browser.dialog")] [InlineData(true, true, false, "browser.dialog")]
    public async Task Tenant_Browser_model_reads_use_scoped_Allowed_or_exact_Review_and_preserve_compacted_delivery_scope(bool sqlite, bool warm, bool review, string tool)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Read browser", ["browser.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read own Session", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId);
            if (warm)
            {
                // Trusted disposable fixture setup is separate from the model's read authority.
                Assert.Null(await native.EnsureTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
                var command = new BrowserNavigate(native.Fixture.Origin + "/browser-native.html");
                var prepared = await native.PrepareTenantCommandAsync(scope, command);
                Assert.Null((await native.ExecuteAsync(new(sessionId, command) { TenantOwner = scope, TenantWitness = prepared.Witness })).ErrorCode);
            }
            if (tool == "browser.dialog")
            {
                // The privileged fixture trigger is outside model read authority; inspect must neither resolve nor expose its message.
                var trigger = new BrowserClick(new("role", "button", "Confirm")); var prepared = await native.PrepareTenantCommandAsync(scope, trigger);
                Assert.Equal("dialog_pending", (await native.ExecuteAsync(new(sessionId, trigger) { TenantOwner = scope, TenantWitness = prepared.Witness })).ErrorCode);
            }
            model.ProposedBrowserRead = new("read", tool, tool == "browser.tabs" ? """{"operation":"list"}""" : tool == "browser.dialog" ? """{"operation":"inspect"}""" : tool == "browser.get_config" ? "" : "{}");
            model.BrowserReadRepeats = tool == "browser.snapshot" ? 2 : 1;
            if (review)
            {
                await factory.Services.GetRequiredService<TenantOperationPolicyService>().SaveAsync(tenant,
                    (await factory.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password")) is { } login
                        ? new(login.Account.AccountId, login.Session.SessionId, human) : throw new InvalidOperationException(),
                    "browser.read", new(OperationApprovalMode.Always, false), await Revision(owner, tenant), 0);
                model.ProposedBrowserCommand = """{"operation":"run_browser_command","command":{"tool":"browser.get_config","arguments":{}}}""";
            }
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the configured source", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid(); if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? completed = null;
            if (review)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(25)); AgentRun? waiting;
                do { waiting = await runs.FindTenantRunAsync(tenant, runId); if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break; await Task.Delay(25, wait.Token); } while (!wait.IsCancellationRequested);
                Assert.NotNull(waiting); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                Assert.Equal(0, (await native.PrepareTenantCommandAsync(scope, new BrowserGetConfig())).Witness!.CommandRevision);
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("browser.read", request.Permission);
                Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve",
                    actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            }
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await CurrentApprovalExecution(runs, tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            if (!review) { Assert.Null(completed.ApprovalRequestId); Assert.Equal(AgentRunSideEffectDisposition.None, completed.SideEffect.Disposition); }
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var transcript));
            var results = transcript!.Where(m => m.Role == ModelRole.Tool && m.Name == model.ProposedBrowserRead.Name).ToArray(); Assert.Equal(model.BrowserReadRepeats, results.Length);
            if (review)
            {
                using var failedRead = JsonDocument.Parse(results[0].Text); Assert.Equal("browser_read_review_required", failedRead.RootElement.GetProperty("reasonCode").GetString());
                var authorized = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization);
                using var approvedRead = JsonDocument.Parse(authorized.Text); Assert.False(approvedRead.RootElement.GetProperty("observationUnavailable").GetBoolean());
            }
            else Assert.All(results, result => { using var json = JsonDocument.Parse(result.Text); Assert.True(json.RootElement.GetProperty("tenantBrowserRead").GetBoolean());
                Assert.Equal(64, json.RootElement.GetProperty("readCommandHash").GetString()!.Length); Assert.True(json.RootElement.GetProperty("untrustedBrowserContent").GetBoolean()); });
            if (tool == "browser.snapshot") { using var first = JsonDocument.Parse(results[0].Text); Assert.True(first.RootElement.GetProperty("compacted").GetBoolean()); Assert.Contains("contentExcerpt", results[0].Text); }
            if (tool == "browser.tabs") { using var listed = JsonDocument.Parse(results[0].Text); Assert.Single(listed.RootElement.GetProperty("tabs").EnumerateArray()); }
            if (tool == "browser.dialog")
            {
                using var inspected = JsonDocument.Parse(results[0].Text); Assert.Equal("confirm", inspected.RootElement.GetProperty("kind").GetString());
                Assert.True(inspected.RootElement.GetProperty("messageRedacted").GetBoolean()); Assert.Equal("[redacted]", inspected.RootElement.GetProperty("message").GetString());
                var dismiss = new BrowserDialog("dismiss"); var prepared = await native.PrepareTenantCommandAsync(scope, dismiss);
                Assert.NotNull(prepared.Witness!.DialogId); // The read left the original modal pending.
                Assert.Null((await native.ExecuteAsync(new(sessionId, dismiss) { TenantOwner = scope, TenantWitness = prepared.Witness })).ErrorCode); // Trusted fixture cleanup.
            }
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == model.ProposedBrowserRead.Name));
            Assert.Equal(warm ? 1 : 0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts);
            if (!review) Assert.DoesNotContain(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action is "approvals.request" or "operations.browser.command" or "operations.browser.command.dispatch");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
