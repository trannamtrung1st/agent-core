using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using AgentCore.Infrastructure.Browser;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantBrowserChromiumTheoryAttribute : TheoryAttribute
    {
        public TenantBrowserChromiumTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("AGENTCORE_CHROMIUM_ISOLATION_TESTS") != "1")
                Skip = "Opt in with AGENTCORE_CHROMIUM_ISOLATION_TESTS=1; uses disposable native Browser contexts.";
        }
    }

    [TenantBrowserChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_HTTP_active_counts_use_exact_canonical_owner_and_never_return_page_content(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-active-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null;
                options.FixtureEnabled = true; options.FixturePort = 0; options.PolicyMode = "Restricted";
                options.ProfileMode = "EphemeralSession"; options.NavigationOrigins = ["http://127.0.0.1:5091"];
                options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            async Task<Guid> CreateSession() => (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var first = await CreateSession(); var second = await CreateSession();
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, first))!;
            var scope = new TenantBrowserOwner(tenant, instance, saved.Owner.ExecutorPrincipalId, first);
            var browser = factory.Services.GetRequiredService<IBrowser>(); Assert.True(browser.IsAvailable);
            var bindings = Assert.IsAssignableFrom<ITenantBrowserBinding>(browser);
            Assert.Null(await bindings.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var fixtureUrl = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?private=scope-test";
            // Trusted native fixture warmup is distinct from an IAM-authorized model effect.
            var native = Assert.IsType<NativePlaywrightBrowser>(browser);
            Assert.True(native.Fixture.IsAvailable); Assert.Equal(native.Fixture.Origin, browser.HostPolicy.NavigationOrigins.Single());
            Assert.Null((await browser.ExecuteAsync(new(first, new BrowserNavigate(fixtureUrl)) { TenantOwner = scope })).ErrorCode);
            var revision = await Revision(owner, tenant);
            var read = await owner.GetAsync(api + $"/sessions/{first}/browser"); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
            var view = await Json(read); Assert.Equal(1, view.GetProperty("observation").GetProperty("liveContexts").GetInt32());
            Assert.Equal(1, view.GetProperty("observation").GetProperty("openPages").GetInt32());
            Assert.Equal("isolated", view.GetProperty("observation").GetProperty("sharing").GetString());
            Assert.DoesNotContain("scope-test", view.GetRawText()); Assert.DoesNotContain("browser-native", view.GetRawText());
            var sibling = await Json(await owner.GetAsync(api + $"/sessions/{second}/browser"));
            Assert.Equal(0, sibling.GetProperty("observation").GetProperty("openPages").GetInt32());
            Assert.Equal("unknown", sibling.GetProperty("observation").GetProperty("sharing").GetString());
            Assert.Equal(revision, await Revision(owner, tenant));
            await bindings.ReleaseTenantAsync(scope);
            var released = await Json(await owner.GetAsync(api + $"/sessions/{first}/browser"));
            Assert.Equal(0, released.GetProperty("observation").GetProperty("openPages").GetInt32());
            Assert.Equal("isolated", released.GetProperty("observation").GetProperty("sharing").GetString());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_HTTP_reads_only_scoped_state_without_provisioning_or_legacy_authority(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
            var id = session.GetProperty("sessionId").GetGuid(); var path = api + $"/sessions/{id}/browser";
            var revision = await Revision(owner, tenant); var read = await owner.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
            var view = await Json(read); Assert.Equal(id, view.GetProperty("sessionId").GetGuid()); Assert.Equal(revision, view.GetProperty("securityRevision").GetInt64());
            Assert.Equal("browser", view.GetProperty("observation").GetProperty("kind").GetString());
            var available = factory.Services.GetRequiredService<IBrowser>().IsAvailable;
            Assert.Equal(available ? "available" : "unavailable", view.GetProperty("observation").GetProperty("availability").GetString());
            Assert.Equal("unknown", view.GetProperty("observation").GetProperty("sharing").GetString());
            if (available) Assert.Equal(0, view.GetProperty("observation").GetProperty("openPages").GetInt32());
            else Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("observation").GetProperty("openPages").ValueKind);
            Assert.Equal(revision, await Revision(owner, tenant));
            Assert.DoesNotContain("profileRoot", view.GetRawText()); Assert.DoesNotContain("tenantOwner", view.GetRawText());
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
            var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Browser Beta", slug = "browser-beta" }))).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{id}/browser")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/sessions/{Guid.NewGuid()}/browser")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsJsonAsync(path, new { command = "navigate", tenantOwner = new { tenantId = beta } })).StatusCode);
            var iam = factory.Services.GetRequiredService<IIamStore>(); var identities = factory.Services.GetRequiredService<IdentityService>();
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var state = (await iam.ReadTenantAsync(tenant))!; var actor = state.Users.Single().PrincipalId;
            var account = await identities.RegisterAsync("browser-reader@example.test", "Browser reader", "explicit-browser-reader-password");
            var role = await access.SaveRoleAsync(actor, tenant, null, "Session and Browser read", ["sessions.read", "browser.read"], await Revision(owner, tenant));
            var invite = await access.InviteAsync(actor, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            await access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
            using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(reader, account.LoginKey, "explicit-browser-reader-password");
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/agents/{instance}")).StatusCode);
            await access.SaveRoleAsync(actor, tenant, role.RoleId, role.Name, ["sessions.read"], await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(path)).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
