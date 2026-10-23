using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Expired_invitation_HTTP_cannot_inspect_register_or_accept_and_fresh_invitation_preserves_account_identity(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-invitation-expiry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            void Configure(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure)
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            using var owner = NewClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var roles = await Json(await owner.GetAsync(api + "/roles"));
            var reader = roles.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
            const string newLogin = "expiry-new@example.test", existingLogin = "expiry-existing@example.test", password = "synthetic-invitation-expiry-password";
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var existing = await identity.RegisterAsync(existingLogin, "Existing account without a membership", password);
            async Task<AgentCore.Api.Security.IdentityEndpoints.InvitationProof> Invite(string login)
            {
                var result = await owner.PostAsJsonAsync(api + "/members/invite", new { login, roleId = reader, expectedRevision = await Revision(owner, tenant) });
                Assert.Equal(HttpStatusCode.Created, result.StatusCode); var value = await Json(result);
                return new(tenant, value.GetProperty("invitationId").GetGuid(), value.GetProperty("token").GetString()!);
            }
            var oldNew = await Invite(newLogin); var oldExisting = await Invite(existingLogin);
            clock.Advance(TimeSpan.FromDays(8));
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            using var anonymous = NewClient(); await Csrf(anonymous);
            foreach (var proof in new[] { oldNew, oldExisting })
            {
                var refused = await anonymous.PostAsJsonAsync("/api/v2/auth/invitation", proof);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("invitation_unavailable", (await Json(refused)).GetProperty("reasonCode").GetString());
            }
            var registration = await anonymous.PostAsJsonAsync("/api/v2/auth/register", new { login = newLogin, displayName = "Not created by an expired proof", password, invitation = oldNew });
            Assert.Equal(HttpStatusCode.Forbidden, registration.StatusCode);
            Assert.Null(await factory.Services.GetRequiredService<IIamStore>().FindAccountAsync(IdentityService.NormalizeLogin(newLogin)));
            using var member = NewClient(); await Login(member, existingLogin, password);
            Assert.Equal(0, (await Json(await member.GetAsync("/api/v2/account/tenants"))).GetProperty("total").GetInt32());
            var acceptance = await member.PostAsJsonAsync($"/api/v2/account/tenant-invitations/{oldExisting.InvitationId}/accept", oldExisting);
            Assert.Equal(HttpStatusCode.Forbidden, acceptance.StatusCode);
            Assert.Equal("invitation_unavailable", (await Json(acceptance)).GetProperty("reasonCode").GetString());
            Assert.Equal(1, (await Json(await owner.GetAsync(api + "/members"))).GetProperty("total").GetInt32());

            var freshExisting = await Invite(existingLogin); var freshNew = await Invite(newLogin);
            Assert.NotEqual(oldExisting.InvitationId, freshExisting.InvitationId); Assert.NotEqual(oldNew.InvitationId, freshNew.InvitationId);
            Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"/api/v2/account/tenant-invitations/{freshExisting.InvitationId}/accept", freshExisting)).StatusCode);
            Assert.Equal(existing.AccountId, (await Json(await member.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid());
            Assert.Equal(1, (await Json(await member.GetAsync("/api/v2/account/tenants"))).GetProperty("total").GetInt32());
            var created = await anonymous.PostAsJsonAsync("/api/v2/auth/register", new { login = newLogin, displayName = "Fresh invitation account", password, invitation = freshNew });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(3, (await Json(await owner.GetAsync(api + "/members"))).GetProperty("total").GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
