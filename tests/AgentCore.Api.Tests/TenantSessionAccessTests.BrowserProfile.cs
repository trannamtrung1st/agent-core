using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory] [InlineData(false, "reset")] [InlineData(true, "reset")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "cold")] [InlineData(true, "cold")]
    [InlineData(false, "close")] [InlineData(true, "close")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    public async Task Tenant_Browser_profile_HTTP_reset_owns_persistent_state_and_never_repeats_an_uncertain_operation(bool sqlite, string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-profile-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "PersistentAgent";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            async Task<Guid> Agent() => (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var instance = await Agent(); var unrelated = await Agent(); var url = api + $"/agents/{instance}/browser/profile";
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var beforeRead = await Json(await owner.GetAsync(url)); Assert.False(beforeRead.GetProperty("profilePresent").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(beforeRead.GetProperty("agentName").GetString())); Assert.Equal("Active", beforeRead.GetProperty("lifecycle").GetString());
            Assert.False(Directory.Exists(Path.Combine(root, "browser", "tenants"))); // Read never binds, creates or launches a profile.
            if (scenario == "cold")
            {
                var invalid = await owner.PostAsJsonAsync(url + "/reset", new { operationId = Guid.NewGuid(), expectedRevision = beforeRead.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = 1, witness = beforeRead.GetProperty("witness").GetString(), executorPrincipalId = Guid.NewGuid() });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.False(Directory.Exists(Path.Combine(root, "browser", "tenants")));
                var resetCold = await Reset(beforeRead); Assert.Equal("reset", resetCold.GetProperty("status").GetString());
                Assert.False((await Json(await owner.GetAsync(url))).GetProperty("profilePresent").GetBoolean()); return;
            }
            async Task<TenantBrowserOwner> Session(Guid agent)
            {
                var id = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = agent, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, id))!;
                return new(tenant, agent, saved.Owner.ExecutorPrincipalId, id);
            }
            async Task<Microsoft.Playwright.IPage> Open(TenantBrowserOwner scope, bool persistent)
            {
                Assert.Null(await native.BindTenantIsolationAsync(scope, persistent ? BrowserProfileMode.PersistentAgent : BrowserProfileMode.EphemeralSession));
                var targetUrl = native.Fixture.Origin + "/browser-native.html?profile=" + scope.SessionId; var command = new BrowserNavigate(targetUrl);
                var prepared = await native.PrepareTenantCommandAsync(scope, command); Assert.Null(prepared.ErrorCode);
                Assert.Null((await native.ExecuteAsync(new(scope.SessionId, command) { TenantOwner = scope, TenantWitness = prepared.Witness })).ErrorCode);
                return native.ContextFor(scope.SessionId)!.Pages.Single(p => p.Url == targetUrl);
            }
            var first = await Session(instance); var second = await Session(instance); var ephemeral = await Session(instance); var other = await Session(unrelated);
            var a = await Open(first, true); var b = await Open(second, true); var e = await Open(ephemeral, false); var c = await Open(other, true);
            await a.EvaluateAsync("() => localStorage.setItem('saved','private profile state')");
            var before = await Json(await owner.GetAsync(url)); Assert.Equal(2, before.GetProperty("liveViews").GetInt32());
            Assert.True(before.GetProperty("canReset").GetBoolean()); var operation = Guid.NewGuid(); var deletes = 0;
            if (scenario == "stale")
            {
                var command = new BrowserObserve(); var prepared = await native.PrepareTenantCommandAsync(first, command);
                Assert.Null((await native.ExecuteAsync(new(first.SessionId, command) { TenantOwner = first, TenantWitness = prepared.Witness })).ErrorCode);
                var denied = await Send(before, operation); Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
                Assert.False(a.IsClosed); Assert.Null((await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.BrowserProfileReset); return;
            }
            if (scenario == "unknown") native.TenantProfileDeleteProbe = _ => { deletes++; throw new IOException("private host path must not leak"); };
            if (scenario == "logout") native.TenantProfileBeforeDeleteProbe = async () => Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            if (scenario == "close") native.ExplicitCloseProbe = _ => Task.CompletedTask;
            var result = await Json(await Send(before, operation));
            Assert.Equal(scenario is "unknown" or "close" or "logout" ? "unknown" : "reset", result.GetProperty("status").GetString());
            Assert.Equal(scenario != "close", a.IsClosed); Assert.Equal(scenario != "close", b.IsClosed); Assert.False(e.IsClosed); Assert.False(c.IsClosed);
            var savedAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            Assert.Equal(operation, savedAgent.Instance.BrowserProfileReset!.OperationId); Assert.Equal(3, savedAgent.Instance.Revision);
            Assert.DoesNotContain("BrowserProfileReset", JsonSerializer.Serialize(savedAgent));
            Assert.DoesNotContain("providerEpoch", result.GetRawText());
            if (scenario is "unknown" or "close" or "logout") Assert.Throws<AccessDeniedException>(() => TenantBrowserOperationRules.RequireProfile(savedAgent, BrowserProfileMode.PersistentAgent));
            TenantBrowserOperationRules.RequireProfile(savedAgent, BrowserProfileMode.EphemeralSession);
            if (scenario == "logout")
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(url)).StatusCode);
                Assert.True(Directory.Exists(Path.Combine(root, "browser", "tenants", tenant.ToString("D"), "agents", instance.ToString("D"))));
                return;
            }
            var repeat = await Json(await Send(before, operation)); Assert.Equal(result.GetProperty("status").GetString(), repeat.GetProperty("status").GetString());
            Assert.Equal(scenario == "unknown" ? 1 : 0, deletes);
            native.TenantProfileDeleteProbe = null; native.ExplicitCloseProbe = null;
            if (scenario is "unknown" or "close" or "logout")
            {
                var refreshed = await Json(await owner.GetAsync(url)); var retry = await Reset(refreshed);
                Assert.Equal("reset", retry.GetProperty("status").GetString());
            }
            var reopened = await Open(first, true); Assert.Null(await reopened.EvaluateAsync<string?>("() => localStorage.getItem('saved')"));
            Assert.False(c.IsClosed); Assert.False(e.IsClosed);
            Assert.DoesNotContain("private host path", (await Json(await owner.GetAsync(url))).GetRawText());
            async Task<HttpResponseMessage> Send(JsonElement view, Guid id) => await owner.PostAsJsonAsync(url + "/reset", new { operationId = id,
                expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = view.GetProperty("instanceRevision").GetInt64(), witness = view.GetProperty("witness").GetString() });
            async Task<JsonElement> Reset(JsonElement view) { var response = await Send(view, Guid.NewGuid()); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await Json(response); }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
