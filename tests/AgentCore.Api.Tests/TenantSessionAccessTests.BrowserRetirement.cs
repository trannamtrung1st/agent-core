using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false, false, "archive")] [InlineData(true, false, "archive")] [InlineData(false, true, "archive")] [InlineData(true, true, "archive")]
    [InlineData(false, false, "pause")] [InlineData(true, false, "pause")] [InlineData(false, true, "pause")] [InlineData(true, true, "pause")]
    [InlineData(false, false, "complete")] [InlineData(true, false, "complete")] [InlineData(false, true, "complete")] [InlineData(true, true, "complete")]
    [InlineData(false, false, "cancel")] [InlineData(true, false, "cancel")] [InlineData(false, true, "cancel")] [InlineData(true, true, "cancel")]
    [InlineData(false, false, "delete")] [InlineData(true, false, "delete")] [InlineData(false, true, "delete")] [InlineData(true, true, "delete")]
    [InlineData(false, false, "agent_archive")] [InlineData(true, false, "agent_archive")] [InlineData(false, true, "agent_archive")] [InlineData(true, true, "agent_archive")]
    [InlineData(false, false, "recover")] [InlineData(true, false, "recover")] [InlineData(false, true, "recover")] [InlineData(true, true, "recover")]
    [InlineData(false, false, "sweep")] [InlineData(true, false, "sweep")] [InlineData(false, true, "sweep")] [InlineData(true, true, "sweep")]
    [InlineData(false, false, "budget")] [InlineData(true, false, "budget")] [InlineData(false, true, "budget")] [InlineData(true, true, "budget")]
    public async Task Tenant_Browser_lifecycle_HTTP_retires_owned_resources_and_recovers_uncertain_closure(bool sqlite, bool persistent, string action)
        => await BrowserRetirementFlow(sqlite, persistent, action);

    [TenantBrowserChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_cleanup_budget_allows_other_owned_retirements_to_progress(bool sqlite)
        => await BrowserRetirementFlow(sqlite, true, "budget_agent");

    private async Task BrowserRetirementFlow(bool sqlite, bool persistent, string action)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-retirement-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
            var instance = await Agent(); var unrelated = await Agent();
            async Task<Guid> Session(Guid agent) => (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = agent, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var first = await Session(instance); var second = await Session(instance); var third = await Session(unrelated);
            if (first.CompareTo(second) > 0) (first, second) = (second, first);
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            async Task<(TenantBrowserOwner Scope, Microsoft.Playwright.IPage Page)> Open(Guid id)
            {
                var saved = (await sessions.FindTenantSessionAsync(tenant, id))!;
                var scope = new TenantBrowserOwner(tenant, saved.Owner.AgentInstanceId, saved.Owner.ExecutorPrincipalId, id);
                Assert.Null(await native.BindTenantIsolationAsync(scope, persistent ? BrowserProfileMode.PersistentAgent : BrowserProfileMode.EphemeralSession));
                var url = native.Fixture.Origin + "/browser-native.html?retirement=" + id; var command = new BrowserNavigate(url);
                var prepared = await native.PrepareTenantCommandAsync(scope, command); Assert.Null(prepared.ErrorCode);
                Assert.Null((await native.ExecuteAsync(new(id, command) { TenantOwner = scope, TenantWitness = prepared.Witness })).ErrorCode);
                return (scope, native.ContextFor(id)!.Pages.Single(p => p.Url == url));
            }
            var a = await Open(first); var b = await Open(second); var c = await Open(third);
            if (action is "recover" or "sweep") { native.ExplicitCloseProbe = _ => Task.CompletedTask; native.TenantRetirementPageCloseProbe = _ => Task.CompletedTask; }
            var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (action is "budget" or "budget_agent") { native.ExplicitCloseProbe = _ => stalled.Task; native.TenantRetirementPageCloseProbe = _ => stalled.Task; }
            HttpResponseMessage response;
            if (action is "agent_archive" or "budget_agent") response = await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
            else if (action == "delete") response = await owner.DeleteAsync(api + $"/sessions/{first}?expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1");
            else response = await owner.PostAsJsonAsync(api + $"/sessions/{first}/{(action is "recover" or "sweep" or "budget" ? "archive" : action)}", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(action == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode);
            var cleanup = factory.Services.GetRequiredService<TenantBrowserRetirementService>();
            if (action is "recover" or "sweep" or "budget")
            {
                Assert.False(a.Page.IsClosed);
                var state = await Json(await owner.GetAsync(api + $"/sessions/{first}/browser"));
                Assert.Equal("cleanup_pending", state.GetProperty("observation").GetProperty("code").GetString());
                Assert.Equal(1, state.GetProperty("observation").GetProperty("cleanupPending").GetInt32());
                Assert.Equal(persistent ? "sharedCoordinated" : "isolated", state.GetProperty("observation").GetProperty("sharing").GetString());
                // Restore does not erase an unconfirmed old-context close; its exact closure is completed on retry.
                native.ExplicitCloseProbe = null; native.TenantRetirementPageCloseProbe = null;
                if (action == "recover") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{first}/restore", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 2 })).StatusCode);
                else { Assert.Equal(1, await cleanup.RecoverAsync()); stalled.TrySetResult(); }
            }
            if (action == "budget_agent")
            {
                Assert.False(a.Page.IsClosed); Assert.False(b.Page.IsClosed);
                var original = a.Page.Context;
                native.ExplicitCloseProbe = context => ReferenceEquals(context, original) ? stalled.Task : context.CloseAsync();
                native.TenantRetirementPageCloseProbe = page => ReferenceEquals(page, a.Page) ? stalled.Task : page.CloseAsync();
                Assert.Equal(0, await cleanup.RecoverAsync()); Assert.False(a.Page.IsClosed); Assert.False(b.Page.IsClosed);
                // The timed-out owner's cursor advances. The next pass reaches its sibling instead of waiting for it again.
                Assert.Equal(1, await cleanup.RecoverAsync()); Assert.False(a.Page.IsClosed); Assert.True(b.Page.IsClosed); Assert.False(c.Page.IsClosed);
                native.ExplicitCloseProbe = null; native.TenantRetirementPageCloseProbe = null;
                Assert.Equal(1, await cleanup.RecoverAsync()); stalled.TrySetResult();
            }
            Assert.True(a.Page.IsClosed); Assert.Equal(action is "agent_archive" or "budget_agent", b.Page.IsClosed); Assert.False(c.Page.IsClosed);
            Assert.Equal(0, await cleanup.RecoverAsync()); Assert.False(c.Page.IsClosed);
            Assert.Null(native.ContextFor(first));
            if (action == "delete") Assert.Null(await sessions.FindTenantSessionAsync(tenant, first));
            else
            {
                var read = await Json(await owner.GetAsync(api + $"/sessions/{first}/browser"));
                Assert.Equal(0, read.GetProperty("observation").GetProperty("liveContexts").GetInt32());
            }
            if (action is "archive" or "pause" or "recover")
            {
                var saved = (await sessions.FindTenantSessionAsync(tenant, first))!;
                if (action != "recover") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{first}/{(action == "archive" ? "restore" : "resume")}",
                    new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = saved.Snapshot.Revision })).StatusCode);
                var fresh = await Open(first); Assert.False(fresh.Page.IsClosed); Assert.False(c.Page.IsClosed);
                Assert.Equal(0, await cleanup.RecoverAsync()); Assert.False(fresh.Page.IsClosed);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
