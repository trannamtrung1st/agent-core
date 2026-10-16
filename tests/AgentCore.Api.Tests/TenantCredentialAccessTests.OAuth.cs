using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData("EmailAccount")]
    [InlineData("GoogleDriveAccount")]
    public async Task Tenant_OAuth_HTTP_connects_without_an_Account_cookie_and_disconnects_locally(string purpose)
    {
        await using var factory = new OAuthFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/credentials";
        var created = await Json(await owner.PostAsJsonAsync(root, new TenantCredentialCreateRequest("Connect account", "Token", purpose,
            await Revision(owner, tenant), EmailAccount: purpose == "EmailAccount" ? new("Gmail", "") : null)));
        var id = created.GetProperty("credential").GetProperty("credentialId").GetGuid(); var path = root + $"/{id}/oauth";
        var setup = await Json(await owner.GetAsync(path));
        Assert.True(setup.GetProperty("configured").GetBoolean()); Assert.True(setup.GetProperty("synthetic").GetBoolean());
        var start = await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode); Assert.True(start.Headers.CacheControl?.NoStore);
        var body = await Json(start); var url = body.GetProperty("authorizationUrl").GetString()!;
        Assert.StartsWith("/api/v2/integrations/tenant-google/oauth/synthetic?state=", url);
        Assert.DoesNotContain("Correlation", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var cookie = Assert.Single(start.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("path=/api/v2/integrations/tenant-google/oauth", cookie);
        var state = Uri.UnescapeDataString(url.Split("?state=")[1]);
        using var callback = Client(factory); callback.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await callback.GetAsync("/api/v2/account/me")).StatusCode);
        var consent = await callback.GetAsync(url); Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Contains("Connect Synthetic", await consent.Content.ReadAsStringAsync());
        Assert.True(consent.Headers.CacheControl?.NoStore); Assert.Equal("no-referrer", Assert.Single(consent.Headers.GetValues("Referrer-Policy")));
        var completed = await callback.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["decision"] = "authorize" }));
        Assert.Equal(HttpStatusCode.SeeOther, completed.StatusCode);
        Assert.Equal($"/t/{tenant:D}/integrations/credentials?credentialId={id:D}&connection=connected", completed.Headers.Location!.OriginalString);
        var connected = await Json(await owner.GetAsync(root + $"/{id}")); Safe(connected);
        var profile = connected.GetProperty("credential").GetProperty(purpose == "EmailAccount" ? "emailAccount" : "driveAccount");
        Assert.Equal("Connected", profile.GetProperty("connectionStatus").GetString()); Assert.True(profile.GetProperty("synthetic").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await callback.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["decision"] = "authorize" }))).StatusCode);
        var disconnected = await owner.PostAsJsonAsync(path + "/disconnect", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 2));
        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode); var result = await Json(disconnected); Safe(result);
        Assert.True(result.GetProperty("providerRevoked").GetBoolean());
        Assert.Equal("Disconnected", result.GetProperty("credential").GetProperty(purpose == "EmailAccount" ? "emailAccount" : "driveAccount").GetProperty("connectionStatus").GetString());
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }
    [Fact]
    public async Task Tenant_OAuth_HTTP_rejects_missing_proof_wrong_purpose_stale_attempts_and_revoked_sessions()
    {
        await using var factory = new OAuthFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/credentials";
        var id = (await Json(await owner.PostAsJsonAsync(root, new TenantCredentialCreateRequest("OAuth", "Token", "GoogleDriveAccount", await Revision(owner, tenant)))))
            .GetProperty("credential").GetProperty("credentialId").GetGuid();
        var path = root + $"/{id}/oauth";
        using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(1, 1))).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(1, 1))).StatusCode);
        await Csrf(owner);
        var started = await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var url = (await Json(started)).GetProperty("authorizationUrl").GetString()!; var state = Uri.UnescapeDataString(url.Split("?state=")[1]);
        var cookie = Assert.Single(started.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var stranger = Client(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(url)).StatusCode);
        using var callback = Client(factory); callback.DefaultRequestHeaders.Add("Cookie", cookie);
        await factory.Services.GetRequiredService<IdentityService>().RegisterAsync("mixup@example.test", "Other Account", "strong-mixup-password");
        using (var other = Client(factory))
        {
            await Csrf(other);
            var login = await other.PostAsJsonAsync("/api/v2/auth/login", new { login = "mixup@example.test", password = "strong-mixup-password" });
            var accountCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("agentcore.session=", StringComparison.Ordinal)).Split(';')[0];
            using var mixed = Client(factory); mixed.DefaultRequestHeaders.Add("Cookie", accountCookie + "; " + cookie);
            Assert.Equal(HttpStatusCode.OK, (await mixed.GetAsync("/api/v2/account/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await callback.GetAsync("/api/v2/integrations/tenant-google/oauth/callback?state=" + Uri.EscapeDataString(state) + "&code=private-code")).StatusCode);
        var cancelled = await callback.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["decision"] = "cancel" }));
        Assert.Equal(HttpStatusCode.SeeOther, cancelled.StatusCode); Assert.EndsWith("connection=denied", cancelled.Headers.Location!.OriginalString);
        var second = await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var secondUrl = (await Json(second)).GetProperty("authorizationUrl").GetString()!;
        var third = await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var thirdUrl = (await Json(third)).GetProperty("authorizationUrl").GetString()!;
        callback.DefaultRequestHeaders.Remove("Cookie"); callback.DefaultRequestHeaders.Add("Cookie", Assert.Single(second.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.Forbidden, (await callback.GetAsync(secondUrl)).StatusCode);
        callback.DefaultRequestHeaders.Remove("Cookie"); callback.DefaultRequestHeaders.Add("Cookie", Assert.Single(third.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await callback.GetAsync(thirdUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await callback.GetAsync(thirdUrl)).StatusCode);
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Tenant_OAuth_HTTP_pending_attempt_and_private_proof_survive_SQLite_host_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-oauth-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant, id; string state, cookie;
        try
        {
            await using (var first = new OAuthFactory(root, true, true))
            {
                using var owner = Client(first); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
                var created = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/credentials", new TenantCredentialCreateRequest("Restart OAuth", "Token", "GoogleDriveAccount", await Revision(owner, tenant)));
                id = (await Json(created)).GetProperty("credential").GetProperty("credentialId").GetGuid();
                var started = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/credentials/{id}/oauth/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
                var url = (await Json(started)).GetProperty("authorizationUrl").GetString()!;
                state = Uri.UnescapeDataString(url.Split("?state=")[1]); cookie = Assert.Single(started.Headers.GetValues("Set-Cookie")).Split(';')[0];
                Assert.Equal(0, first.Outbound); Assert.Equal(0, first.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
            }
            await using (var second = new OAuthFactory(root, true, true))
            {
                using var callback = Client(second); callback.DefaultRequestHeaders.Add("Cookie", cookie);
                var completed = await callback.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["decision"] = "authorize" }));
                Assert.Equal(HttpStatusCode.SeeOther, completed.StatusCode); Assert.EndsWith("connection=connected", completed.Headers.Location!.OriginalString);
                using var owner = Client(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var retained = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/credentials/{id}")); Safe(retained);
                Assert.Equal("Connected", retained.GetProperty("credential").GetProperty("driveAccount").GetProperty("connectionStatus").GetString());
                Assert.Equal(0, second.Outbound); Assert.Equal(0, second.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Tenant_OAuth_HTTP_preserves_host_disable_and_rejects_invalid_consent_forms()
    {
        await using var factory = new OAuthFactory(driveEnabled: false); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var created = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/credentials", new TenantCredentialCreateRequest("Disabled host", "Token", "GoogleDriveAccount", await Revision(owner, tenant))));
        var id = created.GetProperty("credential").GetProperty("credentialId").GetGuid(); var path = $"/api/v2/tenants/{tenant}/credentials/{id}/oauth";
        Assert.False((await Json(await owner.GetAsync(path))).GetProperty("hostEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(path + "/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1))).StatusCode);
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await anonymous.PostAsJsonAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new { state = "private-query-sentinel" })).StatusCode);
        var badForm = await anonymous.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = new string('x', 300) }));
        Assert.Equal(HttpStatusCode.BadRequest, badForm.StatusCode); Assert.DoesNotContain(new string('x', 300), await badForm.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }
    private sealed class OAuthFactory(string? existingRoot = null, bool sqlite = false, bool preserve = false, bool driveEnabled = true) : AgentCoreApiFactory
    {
        public string Root { get; } = existingRoot ?? Path.Combine(Path.GetTempPath(), "tenant-oauth-api-" + Guid.NewGuid().ToString("N"));
        public int Outbound;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?>
        {
            ["GoogleOAuth:ClientId"] = "unused-real-client", ["GoogleOAuth:ClientSecret"] = "unused-real-secret",
            ["GoogleOAuth:PublicAppUrl"] = "https://public.example.test", ["Persistence:WorkspaceRoot"] = Path.Combine(Root, "workspaces"),
            ["GoogleDrive:Enabled"] = driveEnabled.ToString(),
            ["Persistence:ArtifactRoot"] = Path.Combine(Root, "artifacts")
        };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(Root, "keys");
                options.AttachmentRoot = Path.Combine(Root, "attachments");
                options.ArtifactRoot = Path.Combine(Root, "artifacts");
                options.DefinitionResourceRoot = Path.Combine(Root, "definition-resources");
                if (sqlite)
                {
                    var connection = $"Data Source={Path.Combine(Root, "oauth.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection));
                    TestSqliteIdentity.Configure(services, connection, Path.Combine(Root, "keys"));
                    services.RemoveAll<ICredentialStore>(); services.AddSingleton<ICredentialStore, SqliteCredentialStore>();
                    services.RemoveAll<IAgentCredentialBindingStore>(); services.AddSingleton<IAgentCredentialBindingStore, SqliteCredentialStore>();
                }
                services.AddHttpClient(TenantGoogleOAuthProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new NoOAuthNetwork(this));
            });
        }
        public override async ValueTask DisposeAsync()
        { await base.DisposeAsync(); SqliteConnection.ClearAllPools(); if (!preserve && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class NoOAuthNetwork(OAuthFactory factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref factory.Outbound); throw new InvalidOperationException("Synthetic OAuth attempted network access."); }
    }
}
