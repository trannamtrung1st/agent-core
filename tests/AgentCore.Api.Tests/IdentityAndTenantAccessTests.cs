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
        owner.DefaultRequestHeaders.Add("X-AgentCore-Owner-Capability", RetiredOwnerHeader.Token);
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
        var ownerAccount = await Json(await owner.GetAsync("/api/v2/account/me"));
        Assert.Contains(ownerAccount.GetProperty("systemPermissions").EnumerateArray(), p => p.GetString() == "system.tenants.create");
        var readerAccount = await Json(await reader.GetAsync("/api/v2/account/me"));
        Assert.Empty(readerAccount.GetProperty("systemPermissions").EnumerateArray());
        var effective = await Json(await reader.GetAsync($"/api/v2/tenants/{firstTenant:D}/access/me"));
        Assert.Contains(effective.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "agents.read");
        Assert.DoesNotContain(effective.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "agents.chat");
        var searched = await Json(await owner.GetAsync($"/api/v2/tenants/{firstTenant:D}/members?q=reader"));
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
        Assert.Equal("READER@EXAMPLE.TEST", searched.GetProperty("items")[0].GetProperty("login").GetString());
        var invitations = await Json(await owner.GetAsync($"/api/v2/tenants/{firstTenant:D}/members/invitations"));
        Assert.Equal("Accepted", invitations.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.False(invitations.GetProperty("items")[0].TryGetProperty("tokenHash", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{firstTenant:D}/members/invitations")).StatusCode);
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
    public async Task Exact_resource_assignments_require_owned_targets_and_revocation_applies_to_the_next_request()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await RefreshCsrf(owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v2/auth/login", new { login = "owner@example.test", password = "explicit-synthetic-test-password" })).StatusCode);
        await RefreshCsrf(owner);
        var a = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var second = await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other organization", slug = "other-organization" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var b = (await Json(second)).GetProperty("tenantId").GetGuid();
        var roles = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/roles"));
        var initialRole = roles.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "agent-operator").GetProperty("roleId").GetGuid();
        var invitation = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/members/invite", new { login = "scoped@example.test", roleId = initialRole, expectedRevision = roles.GetProperty("revision").GetInt64() }));
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await RefreshCsrf(member);
        var proof = new { tenantId = a, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() };
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "scoped@example.test", displayName = "Scoped inspector", password = "strong-scoped-password", invitation = proof })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/api/v2/auth/login", new { login = "scoped@example.test", password = "strong-scoped-password" })).StatusCode);
        await RefreshCsrf(member);
        var members = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/members"));
        var target = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "OWNER@EXAMPLE.TEST").GetProperty("userId").GetGuid();
        var subject = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "SCOPED@EXAMPLE.TEST");
        var principal = subject.GetProperty("principalId").GetGuid();
        var ownMemberId = subject.GetProperty("userId").GetGuid();
        var foreign = (await Json(await owner.GetAsync($"/api/v2/tenants/{b}/members"))).GetProperty("items")[0].GetProperty("userId").GetGuid();
        var savedRole = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/roles", new { name = "One member inspector", permissions = new[] { "members.read", "iam.roles.read" }, expectedRevision = members.GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.Created, savedRole.StatusCode);
        var roleId = (await Json(savedRole)).GetProperty("roleId").GetGuid();
        roles = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/roles"));
        var revision = roles.GetProperty("revision").GetInt64();
        var assigned = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/role-assignments", new { principalId = principal, roleId, expectedRevision = revision, resourceType = "member", resourceId = target.ToString("N") });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var assignment = await Json(assigned);
        Assert.Equal(target.ToString("D"), assignment.GetProperty("scope").GetProperty("resourceId").GetString());
        var assignmentId = assignment.GetProperty("assignmentId").GetGuid();
        var resource = $"/api/v2/tenants/{a}/access/resources/member/{target:D}";
        var access = await Json(await member.GetAsync(resource));
        Assert.Contains(access.GetProperty("assignments").EnumerateArray(), item => item.GetProperty("assignmentId").GetGuid() == assignmentId);
        Assert.Contains(access.GetProperty("assignments").EnumerateArray(), item => item.GetProperty("scope").GetProperty("kind").GetInt32() == (int)AccessScopeKind.Tenant);
        var paged = await Json(await member.GetAsync(resource + "?page=0&pageSize=1"));
        Assert.Equal(1, paged.GetProperty("assignments").GetArrayLength());
        Assert.True(paged.GetProperty("total").GetInt32() > 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/access/resources/member/{ownMemberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/roles")).StatusCode);
        var allowed = await member.PostAsJsonAsync($"/api/v2/tenants/{a}/access/check", new { principalId = principal, permission = "members.read", resourceType = "member", resourceId = target });
        Assert.True((await Json(allowed)).GetProperty("allowed").GetBoolean());
        var mismatch = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/access/check", new { principalId = principal, permission = "members.read", resourceType = "role", resourceId = roleId });
        Assert.Equal("resource_type_mismatch", (await Json(mismatch)).GetProperty("reasonCode").GetString());
        revision = access.GetProperty("revision").GetInt64();
        var auditCount = (await Json(await owner.GetAsync($"/api/v2/tenants/{a}/audit?limit=200"))).GetProperty("items").GetArrayLength();
        foreach (var targetId in new[] { foreign, Guid.NewGuid() })
        {
            var denied = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/role-assignments", new { principalId = principal, roleId, expectedRevision = revision, resourceType = "member", resourceId = targetId });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("resource_unavailable", (await Json(denied)).GetProperty("reasonCode").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{a}/access/resources/member/{targetId}")).StatusCode);
        }
        Assert.Equal(auditCount, (await Json(await owner.GetAsync($"/api/v2/tenants/{a}/audit?limit=200"))).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v2/tenants/{a}/role-assignments/{assignmentId}?expectedRevision={revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(resource)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v2/account/me")).StatusCode);
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
