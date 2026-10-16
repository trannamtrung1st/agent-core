using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class IdentityAndTenantAccessTests
{
    [Fact]
    public async Task Cookie_csrf_two_tenant_membership_and_revocation_are_enforced_over_http()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v2/account/me")).StatusCode);
        var oldCapability = await factory.Services.GetRequiredService<IOwnerCapabilityService>().IssueAsync();
        owner.DefaultRequestHeaders.Add("X-AgentCore-Owner-Capability", oldCapability.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v2/account/tenants")).StatusCode);
        var missingCsrf = await owner.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal("invalid_csrf", (await Json(missingCsrf)).GetProperty("reasonCode").GetString());
        await RefreshCsrf(owner);
        var signedIn = await owner.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"), value => value.StartsWith("agentcore.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", await signedIn.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await RefreshCsrf(owner);
        var firstList = await Json(await owner.GetAsync("/api/v2/account/tenants"));
        var firstTenant = firstList.GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var create = await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Second organization", slug = "second-organization" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var secondTenant = (await Json(create)).GetProperty("tenantId").GetGuid();
        var two = await Json(await owner.GetAsync("/api/v2/account/tenants"));
        Assert.Equal(2, two.GetProperty("total").GetInt32());
        Assert.NotEqual(two.GetProperty("items")[0].GetProperty("principalId").GetGuid(), two.GetProperty("items")[1].GetProperty("principalId").GetGuid());

        var roles = await Json(await owner.GetAsync($"/api/v2/tenants/{firstTenant:D}/roles"));
        var readerRole = roles.GetProperty("items").EnumerateArray().Single(role => role.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var inviteResponse = await owner.PostAsJsonAsync($"/api/v2/tenants/{firstTenant:D}/members/invite",
            new { login = "reader@example.test", roleId = readerRole, expectedRevision = roles.GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var invite = await Json(inviteResponse);
        Assert.False(invite.TryGetProperty("tokenHash", out _));
        var proof = new { tenantId = firstTenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() };
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await RefreshCsrf(reader);
        var register = await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "reader@example.test", displayName = "Reader", password = "a-strong-reader-password", invitation = proof });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/v2/account/me")).StatusCode);
        var login = await reader.PostAsJsonAsync("/api/v2/auth/login", new { login = "reader@example.test", password = "a-strong-reader-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await RefreshCsrf(reader);
        var memberships = await Json(await reader.GetAsync("/api/v2/account/tenants"));
        Assert.Equal(1, memberships.GetProperty("total").GetInt32());
        var principal = memberships.GetProperty("items")[0].GetProperty("principalId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{secondTenant:D}/members")).StatusCode);
        var read = await reader.PostAsJsonAsync($"/api/v2/tenants/{firstTenant:D}/access/check", new { principalId = principal, permission = "agents.read" });
        Assert.True((await Json(read)).GetProperty("allowed").GetBoolean());
        var chat = await reader.PostAsJsonAsync($"/api/v2/tenants/{firstTenant:D}/access/check", new { principalId = principal, permission = "agents.chat" });
        Assert.False((await Json(chat)).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Escalation", slug = "escalation" })).StatusCode);

        var members = await Json(await owner.GetAsync($"/api/v2/tenants/{firstTenant:D}/members"));
        var readerUser = members.GetProperty("items").EnumerateArray().Single(member => member.GetProperty("principalId").GetGuid() == principal).GetProperty("userId").GetGuid();
        var revoke = await owner.PutAsJsonAsync($"/api/v2/tenants/{firstTenant:D}/members/{readerUser:D}/status", new { status = IdentityStatus.Removed, expectedRevision = members.GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{firstTenant:D}/roles")).StatusCode);
        Assert.Equal(0, (await Json(await reader.GetAsync("/api/v2/account/tenants"))).GetProperty("total").GetInt32());
        var logout = await reader.PostAsync("/api/v2/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/v2/account/me")).StatusCode);
    }

    [Fact]
    public async Task Saved_cookie_is_rejected_after_server_session_revocation_and_no_private_payloads_are_cached()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await RefreshCsrf(client);
        var login = await client.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.CacheControl?.NoStore);
        // Replay the captured cookie from another browser after the original browser logs out.
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("agentcore.session=", StringComparison.Ordinal)).Split(';')[0];
        await RefreshCsrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        using var replay = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v2/account/me")).StatusCode);
    }

    [Fact]
    public async Task Bad_credentials_and_invitations_do_not_create_sessions_or_memberships()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await RefreshCsrf(client);
        foreach (var login in new[] { "owner@example.test", "unknown@example.test" })
        {
            var denied = await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password = "incorrect-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("invalid_credentials", (await Json(denied)).GetProperty("reasonCode").GetString());
            Assert.False(denied.Headers.TryGetValues("Set-Cookie", out _));
        }
        var register = await client.PostAsJsonAsync("/api/v2/auth/register", new
        {
            login = "uninvited@example.test", displayName = "Uninvited", password = "strong-uninvited-password",
            invitation = new { tenantId = Guid.NewGuid(), invitationId = Guid.NewGuid(), token = "fabricated-token" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Null(await factory.Services.GetRequiredService<IIamStore>().FindAccountAsync("UNINVITED@EXAMPLE.TEST"));
    }

    [Fact]
    public async Task Password_change_reauthenticates_and_invalidates_a_second_device_cookie()
    {
        await using var factory = new AgentCoreApiFactory();
        using var first = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var second = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var client in new[] { first, second })
        {
            await RefreshCsrf(client);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" })).StatusCode);
            await RefreshCsrf(client);
        }
        var denied = await first.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "incorrect-password", newPassword = "strong-new-account-password" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v2/account/me")).StatusCode);
        var changed = await first.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "strong-new-account-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v2/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v2/account/me")).StatusCode);
        await RefreshCsrf(first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "strong-new-account-password" })).StatusCode);
    }

    [Fact]
    public async Task Missing_ephemeral_bootstrap_credentials_fail_startup_without_an_implicit_owner()
    {
        await using var factory = new MissingBootstrapFactory();
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Security:EphemeralBootstrap:Password", failure.Message, StringComparison.Ordinal);
    }
    private sealed class MissingBootstrapFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
            new Dictionary<string, string?> { ["Security:EphemeralBootstrap:Password"] = null };
    }

    private static async Task RefreshCsrf(HttpClient client)
    {
        var response = await client.GetAsync("/api/v2/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await Json(response)).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", token);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.Clone(); }
}
