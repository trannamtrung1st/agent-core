using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_User_Profile_HTTP_self_service_is_private_scoped_CSRF_guarded_and_recoverable(bool sqlite)
    {
        await using var factory = new IsolationFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, _, _) = await IsolationAgent(owner); var path = $"/api/v2/tenants/{tenant}/me/profile";
        using var anonymous = factory.CreateClient(); anonymous.DefaultRequestHeaders.Add("X-AgentCore-Owner", "obsolete-fixed-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var beforeResponse = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode); Assert.True(beforeResponse.Headers.CacheControl?.NoStore);
        var before = await Json(beforeResponse); Assert.Equal(0, before.GetProperty("profile").GetProperty("revision").GetInt64()); Assert.Empty(before.GetProperty("profile").GetProperty("preferences").EnumerateObject());
        var update = new { expectedRevision = before.GetProperty("securityRevision").GetInt64(), expectedProfileRevision = 0, values = new { preferredName = "  Native thử nghiệm  ", language = "vi", locale = "vi-VN", timeZone = "Asia/Ho_Chi_Minh" } };
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(path, update)).StatusCode); await Csrf(owner);
        var savedResponse = await owner.PatchAsJsonAsync(path, update); Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode); Assert.True(savedResponse.Headers.CacheControl?.NoStore);
        var saved = await Json(savedResponse); Assert.Equal("Native thử nghiệm", saved.GetProperty("profile").GetProperty("preferences").GetProperty("preferredName").GetProperty("value").GetString());
        Assert.All(saved.GetProperty("profile").GetProperty("preferences").EnumerateObject(), p => Assert.Equal("UserSet", p.Value.GetProperty("source").GetString()));
        Assert.False(saved.GetProperty("profile").TryGetProperty("accountId", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync(path, update)).StatusCode); Assert.Equal(saved.GetRawText(), (await Json(await owner.GetAsync(path))).GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?principalId=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{Guid.NewGuid()}/me/profile")).StatusCode);
        var security = factory.Services.GetRequiredService<IIamStore>(); var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
        var principal = saved.GetProperty("profile").GetProperty("principalId").GetGuid(); var memberAccount = await identity.RegisterAsync("profile-member@example.test", "Member", "strong-member-password");
        var role = await access.SaveRoleAsync(principal, tenant, null, "Only Agent reads", ["agents.read"], await Revision(owner, tenant));
        var invite = await access.InviteAsync(principal, tenant, memberAccount.LoginKey, role.RoleId, await Revision(owner, tenant)); await access.AcceptInvitationAsync(memberAccount.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(member, memberAccount.LoginKey, "strong-member-password");
        var memberRead = await Json(await member.GetAsync(path)); Assert.Empty(memberRead.GetProperty("profile").GetProperty("preferences").EnumerateObject());
        Assert.DoesNotContain("Native thử nghiệm", memberRead.GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync(path + "?accountId=" + memberAccount.AccountId)).StatusCode);
        var current = await Json(await owner.GetAsync(path));
        var cleared = await Json(await owner.PatchAsJsonAsync(path, new { expectedRevision = current.GetProperty("securityRevision").GetInt64(), expectedProfileRevision = 1, values = new Dictionary<string, string?> { ["preferredName"] = null } }));
        Assert.False(cleared.GetProperty("profile").GetProperty("preferences").TryGetProperty("preferredName", out _));
        Assert.Equal(2, (await security.ReadAuditAsync(tenant, 100)).Count(a => a.Action == "profile.update"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_User_Profile_HTTP_rejects_closed_shape_duplicates_invalid_values_and_oversized_input_without_writes(bool sqlite)
    {
        await using var factory = new IsolationFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, _, _) = await IsolationAgent(owner); var path = $"/api/v2/tenants/{tenant}/me/profile"; var before = await Json(await owner.GetAsync(path));
        var security = before.GetProperty("securityRevision").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var invalid = new[] {
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"Secret\"},\"principalId\":\"forged\"}",
            "{\"expectedRevision\":SEC,\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"Secret\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"Secret\",\"preferredName\":\"Other\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{},\"values\":{\"preferredName\":\"Secret\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":{\"value\":\"Secret\",\"source\":\"HostSet\"}}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"systemPrompt\":\"Secret\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"   \"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"Secret\\nvalue\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"language\":\"not_a_language\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":0,\"values\":{\"timeZone\":\"bad zone\"}}",
            "{\"expectedRevision\":SEC,\"expectedProfileRevision\":-1,\"values\":{\"preferredName\":\"Secret\"}}",
            "{\"expectedProfileRevision\":0,\"values\":{\"preferredName\":\"Secret\"}}" };
        foreach (var input in invalid)
        {
            using var body = new StringContent(input.Replace("SEC", security, StringComparison.Ordinal), Encoding.UTF8, "application/json");
            var response = await owner.PatchAsync(path, body); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("Secret", await response.Content.ReadAsStringAsync());
        }
        var tooLarge = await owner.PatchAsJsonAsync(path, new { expectedRevision = long.Parse(security), expectedProfileRevision = 0, values = new { preferredName = new string('x', 5000) } }); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(before.GetRawText(), (await Json(await owner.GetAsync(path))).GetRawText());
        Assert.DoesNotContain(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100), a => a.Action == "profile.update");
    }

    [Fact]
    public async Task Tenant_User_Profile_SQLite_actual_application_restart_preserves_owned_values_without_global_Profile_adoption()
    {
        await using var first = new IsolationFactory(true); using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, _, _) = await IsolationAgent(owner); var path = $"/api/v2/tenants/{tenant}/me/profile"; var before = await Json(await owner.GetAsync(path));
        var saved = await Json(await owner.PatchAsJsonAsync(path, new { expectedRevision = before.GetProperty("securityRevision").GetInt64(), expectedProfileRevision = 0, values = new { preferredName = "Durable private name" } }));
        var contexts = first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
        await using (var db = contexts.CreateDbContext()) { Assert.Single(await db.Set<TenantUserProfileRecord>().ToArrayAsync()); Assert.False(db.Database.HasPendingModelChanges()); }
        await first.DisposeAsync(); await using var reopened = new IsolationFactory(true, first.Root); using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(again, "owner@example.test", "explicit-synthetic-test-password"); Assert.Equal(saved.GetRawText(), (await Json(await again.GetAsync(path))).GetRawText());
    }
}
