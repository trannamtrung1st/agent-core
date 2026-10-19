using System.Net;
using System.Net.Http.Json;
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
    [TenantBrowserChromiumTheory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_model_capture_HTTP_publishes_owned_image_and_rechecks_read_before_each_generation(bool sqlite, bool revoke)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, modelKey = "scripted-vision" }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Capture browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Publish in Session", ["sessions.read", "artifacts.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Independent captured image read", ["artifacts.read"], await Revision(owner, tenant));
            var read = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(native.Fixture.Origin + "/browser-native.html"); var target = await native.PrepareTenantCommandAsync(scope, navigation);
            Assert.Null((await native.ExecuteAsync(new(sessionId, navigation) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
            model.ProposedBrowserCommand = """{"operation":"run_browser_command","command":{"tool":"browser.screenshot","arguments":{}}}""";
            var revoked = 0;
            if (revoke) model.BeforeGenerate = async (request, _) => {
                if (!request.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true) || Interlocked.Exchange(ref revoked, 1) != 0) return;
                await access.RevokeAssignmentAsync(human, tenant, read.AssignmentId, await Revision(owner, tenant));
                model.ProposedBrowserRead = new("after-capture-read", ToolCatalog.BrowserSnapshot, "{}");
            };
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Capture the approved local Browser image", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid(); if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!timeout.IsCancellationRequested)
                { waiting = await runs.FindTenantRunAsync(tenant, runId); if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break; await Task.Delay(25, timeout.Token); }
            Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            AgentRun? completed = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                while (!timeout.IsCancellationRequested)
                { completed = await CurrentApprovalExecution(runs, tenant, runId); if (completed?.IsTerminal == true) break; await Task.Delay(25, timeout.Token); }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
            var file = Assert.Single(TenantBrowserArtifactRules.Read(receipt.BrowserArtifactsJson!).Files);
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true));
            if (revoke) { Assert.Equal(1, revoked); Assert.DoesNotContain(model.Requests.Last().Messages, m => m.Parts?.OfType<ModelImageContent>().Any() == true); }
            Assert.DoesNotContain("base64", completed.Checkpoint!.PayloadJson, StringComparison.OrdinalIgnoreCase);
            var details = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("published", details.GetProperty("operationResult").GetProperty("browserArtifacts").GetProperty("status").GetString());
            using var download = await owner.GetAsync(api + $"/sessions/{sessionId}/artifacts/{file.ArtifactId}/content"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.True((await download.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Single((await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, sessionId, null, null, 100)).Items);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
