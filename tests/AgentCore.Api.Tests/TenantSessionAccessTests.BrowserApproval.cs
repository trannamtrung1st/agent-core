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
    public async Task Tenant_Browser_Review_HTTP_uses_actual_native_target_and_separates_consent_from_dispatch(bool sqlite, bool drift)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-review-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession"; options.NavigationOrigins = ["http://127.0.0.1:5091"];
                options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Review native Browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read native Session", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Prepare Browser review", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.GetProperty("runId").GetGuid()))!; var at = DateTimeOffset.UtcNow;
            run = await factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
            var browser = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); Assert.True(browser.IsAvailable);
            Assert.Equal(browser.Fixture.Origin, browser.HostPolicy.NavigationOrigins.Single());
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await browser.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var url = browser.Fixture.Origin + "/browser-native.html?private=review-evidence";
            var command = new BrowserNavigate(url); var target = await browser.PrepareTenantCommandAsync(scope, command); Assert.Null(target.ErrorCode);
            // Trusted fixture warmup is distinct from a model effect. Review itself performs no native command.
            Assert.Null((await browser.ExecuteAsync(new(sessionId, command) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
            var before = (await browser.PrepareTenantCommandAsync(scope, command)).Witness!;
            var service = factory.Services.GetRequiredService<TenantApprovalService>();
            var request = (await service.PrepareBrowserAsync(new(tenant, run.AgentRunId), ToolCatalog.BrowserNavigate,
                System.Text.Json.JsonSerializer.Serialize(new { url }), run.Revision, run.Claim!.Generation)).Request!;
            Assert.Equal(browser.Fixture.Origin, request.Snapshot!.Browser!.Origin); Assert.DoesNotContain("private=", request.Snapshot.Browser.Origin);
            Assert.Equal(before.CommandRevision, (await browser.PrepareTenantCommandAsync(scope, command)).Witness!.CommandRevision);
            if (drift)
            {
                var inspect = new BrowserGetConfig(); var next = await browser.PrepareTenantCommandAsync(scope, inspect);
                Assert.Null((await browser.ExecuteAsync(new(sessionId, inspect) { TenantOwner = scope, TenantWitness = next.Witness })).ErrorCode);
            }
            var route = api + $"/approvals/{request.RequestId}"; var response = await owner.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var view = await Json(response); Assert.Equal("browser_command", view.GetProperty("preparedAction").GetProperty("operation").GetString());
            var feed = await Json(await owner.GetAsync(api + $"/sessions/{sessionId}/approvals"));
            Assert.Equal("browser_command", Assert.Single(feed.GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
            Assert.Equal(!drift, view.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Contains(view.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "browserArguments" && c.GetProperty("after").GetString()!.Contains("private=review-evidence", StringComparison.Ordinal));
            Assert.False(view.TryGetProperty("tenantWitness", out _)); Assert.False(view.TryGetProperty("targetJson", out _));
            var decision = await owner.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
            Assert.Equal(drift ? HttpStatusCode.Conflict : HttpStatusCode.OK, decision.StatusCode);
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
            Assert.Equal(before.CommandRevision + (drift ? 1 : 0), (await browser.PrepareTenantCommandAsync(scope, command)).Witness!.CommandRevision);
            using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
            if (drift) Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + "/cancel", new { actionHash = request.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
