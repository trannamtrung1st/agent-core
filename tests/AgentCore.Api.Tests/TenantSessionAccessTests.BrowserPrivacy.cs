using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_Browser_privacy_HTTP_requires_platform_human_and_preserves_restart_ceilings_CAS_and_tenant_separation(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-browser-privacy-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> backing = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = backing.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                services.RemoveAll<BrowserPrivacyAuthority>(); services.AddSingleton(new BrowserPrivacyAuthority(true, true, ["https://example.test"], ["https://graphics.test"]));
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.AllowUnmaskedCaptures = true; options.UnmaskedCaptureOrigins = ["https://example.test"]; options.TrustedVisualCaptureOrigins = ["https://graphics.test"];
            }));
            const string path = "/api/v2/system/browser/privacy";
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new { })).StatusCode);
            using var ownerToken = RetiredOwnerHeader.CreateClient(factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await ownerToken.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ownerToken.GetAsync("/api/v2/admin/browser/privacy")).StatusCode);
            using var actor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(actor, "owner@example.test", "explicit-synthetic-test-password");
            var initial = (await actor.GetFromJsonAsync<BrowserPrivacyResponse>(path))!; Assert.Equal("Protected", initial.Effective.Mode); Assert.Equal(0, initial.Saved.Revision); Assert.Equal(sqlite, initial.Durable);
            var change = new SaveBrowserPrivacyRequest(0, "Unmasked", ["https://example.test"], ["https://graphics.test"], false);
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(path, change)).StatusCode);
            foreach (var origin in new[] { "*", "https://example.test/path", "https://user@example.test", "https://example.test?q=secret", "https://*.test", "file:///" })
                Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(path, change with { UnmaskedOrigins = [origin], AcknowledgeExposure = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await actor.PutAsJsonAsync(path, change with { UnmaskedOrigins = ["https://outside.test"], AcknowledgeExposure = true })).StatusCode);
            var savedReply = await actor.PutAsJsonAsync(path, change with { AcknowledgeExposure = true }); savedReply.EnsureSuccessStatusCode();
            var saved = (await savedReply.Content.ReadFromJsonAsync<BrowserPrivacyResponse>())!;
            Assert.Equal("Unmasked", saved.Saved.Mode); Assert.Equal("Protected", saved.Effective.Mode); Assert.True(saved.RestartRequired); Assert.Equal(1, saved.Saved.Revision);
            Assert.Equal(HttpStatusCode.Conflict, (await actor.PutAsJsonAsync(path, change with { AcknowledgeExposure = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await actor.PutAsJsonAsync(path, new { expectedRevision = 1, mode = "Disabled", unmaskedOrigins = Array.Empty<string>(), trustedGraphicsOrigins = Array.Empty<string>(), acknowledgeExposure = false, principalId = Guid.NewGuid() })).StatusCode);
            var raw = await actor.GetAsync(path); Assert.Contains("no-store", raw.Headers.CacheControl!.ToString());
            var tenant = (await Json(await actor.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync($"/api/v2/tenants/{tenant}/browser/privacy")).StatusCode);
            // A full tenant Owner on the same organization still has no platform governance grant.
            var registered = await factory.Services.GetRequiredService<AgentCore.Application.Security.IdentityService>().RegisterAsync("tenant-privacy-owner@example.test", "Tenant privacy owner", "strong-privacy-owner-password");
            var security = factory.Services.GetRequiredService<IIamStore>(); var state = (await security.ReadTenantAsync(tenant))!;
            var access = factory.Services.GetRequiredService<AgentCore.Application.Security.TenantAccessService>();
            var invitation = await access.InviteAsync(state.Users.Single(u => u.AccountId != registered.AccountId).PrincipalId, tenant, registered.LoginKey, state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
            await access.AcceptInvitationAsync(registered.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var tenantOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(tenantOwner, registered.LoginKey, "strong-privacy-owner-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantOwner.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantOwner.PutAsJsonAsync(path, change with { ExpectedRevision = 1 })).StatusCode);
            var updated = await actor.PutAsJsonAsync(path, new SaveBrowserPrivacyRequest(1, "Disabled", [], [], false)); updated.EnsureSuccessStatusCode();
            Assert.Equal("Disabled", (await updated.Content.ReadFromJsonAsync<BrowserPrivacyResponse>())!.Saved.Mode);
            Assert.Equal("Protected", (await actor.GetFromJsonAsync<BrowserPrivacyResponse>(path))!.Effective.Mode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
