using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class AuditReadBarrier(IAuthorizationAuditQueryStore inner) : IAuthorizationAuditQueryStore
    {
        public Func<Task>? After;
        public async ValueTask<AuthorizationAuditPage> QueryAuditAsync(Guid? tenantId, AuthorizationAuditQuery query, CancellationToken ct = default)
        {
            var page = await inner.QueryAuditAsync(tenantId, query, ct);
            if (Interlocked.Exchange(ref After, null) is { } action) await action();
            return page;
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Audit_HTTP_filters_before_count_and_paging_and_keeps_System_facts_separate_from_tenant_activity(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> backing = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            await using var factory = backing.WithWebHostBuilder(b => b.ConfigureTestServices(s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock); }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var initial = clock.GetUtcNow();
            for (var i = 0; i < 3; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await access.SaveRoleAsync(human, tenant, null, "Private role name " + i, ["agents.read"], await Revision(owner, tenant)); }
            var query = api + $"/audit?action=iam.roles.manage&actorPrincipalId={human}&pageSize=1";
            var ids = new HashSet<Guid>();
            for (var page = 0; page < 3; page++)
            {
                var response = await owner.GetAsync(query + $"&page={page}"); Assert.True(response.Headers.CacheControl?.NoStore);
                var result = await Json(response); Assert.Equal(3, result.GetProperty("total").GetInt32()); Assert.Equal(page, result.GetProperty("page").GetInt32());
                var item = Assert.Single(result.GetProperty("items").EnumerateArray()); Assert.Equal(tenant, item.GetProperty("tenantId").GetGuid());
                Assert.True(ids.Add(item.GetProperty("auditId").GetGuid())); Assert.Equal("iam.roles.manage", item.GetProperty("action").GetString());
                Assert.DoesNotContain("Private role name", result.GetRawText());
            }
            Assert.Empty((await Json(await owner.GetAsync(query + "&page=3"))).GetProperty("items").EnumerateArray());
            var instant = Uri.EscapeDataString(initial.AddSeconds(2).ToString("O"));
            Assert.Equal(1, (await Json(await owner.GetAsync(query + $"&from={instant}&through={instant}"))).GetProperty("total").GetInt32());
            Assert.Equal(0, (await Json(await owner.GetAsync(query + "&decision=denied"))).GetProperty("total").GetInt32());
            Assert.Equal(0, (await Json(await owner.GetAsync(api + $"/audit?actorPrincipalId={Guid.NewGuid()}"))).GetProperty("total").GetInt32());
            var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Private other organization", slug = "private-other-audit" }))).GetProperty("tenantId").GetGuid();
            var otherHuman = (await Json(await owner.GetAsync($"/api/v2/tenants/{other}/access/me"))).GetProperty("principalId").GetGuid();
            await access.SaveRoleAsync(otherHuman, other, null, "Other private role", ["agents.read"], await Revision(owner, other));
            Assert.Equal(3, (await Json(await owner.GetAsync(query))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v2/system/browser/privacy", new { expectedRevision = 0, mode = "Disabled", unmaskedOrigins = Array.Empty<string>(), trustedGraphicsOrigins = Array.Empty<string>(), acknowledgeExposure = false })).StatusCode);
            var platform = await Json(await owner.GetAsync("/api/v2/system/audit?action=system.browser.privacy.update&pageSize=1&tenantId=" + tenant));
            Assert.Equal(1, platform.GetProperty("total").GetInt32()); var fact = Assert.Single(platform.GetProperty("items").EnumerateArray());
            Assert.Equal("host", fact.GetProperty("target").GetString()); Assert.Equal(System.Text.Json.JsonValueKind.Null, fact.GetProperty("tenantId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, fact.GetProperty("execution").ValueKind); Assert.DoesNotContain(tenant.ToString("D"), platform.GetRawText());
            Assert.DoesNotContain("Private", platform.GetRawText());
            Assert.Equal(0, (await Json(await owner.GetAsync("/api/v2/system/audit?action=iam.roles.manage"))).GetProperty("total").GetInt32());
            foreach (var suffix in new[] { "?decision=arbitrary", "?action=private%20text", "?actorPrincipalId=00000000-0000-0000-0000-000000000000", "?from=2026-10-22T00%3A00%3A00Z&through=2026-10-21T00%3A00%3A00Z" })
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + "/audit" + suffix)).StatusCode);
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(api + "/audit")).StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v2/system/audit")).StatusCode);
            var identity = factory.Services.GetRequiredService<IdentityService>(); var member = await identity.RegisterAsync("audit-tenant-owner@example.test", "Organization owner", "strong-audit-member-password");
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            var invite = await access.InviteAsync(human, tenant, member.LoginKey, state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
            await access.AcceptInvitationAsync(member.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
            using var localOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(localOwner, member.LoginKey, "strong-audit-member-password");
            Assert.Equal(HttpStatusCode.OK, (await localOwner.GetAsync(api + "/audit")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await localOwner.GetAsync("/api/v2/system/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await localOwner.GetAsync($"/api/v2/tenants/{other}/audit")).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, false, "logout")] [InlineData(true, false, "logout")]
    [InlineData(false, true, "logout")] [InlineData(true, true, "logout")]
    [InlineData(false, false, "password")] [InlineData(true, false, "password")]
    [InlineData(false, true, "password")] [InlineData(true, true, "password")]
    [InlineData(false, false, "revoke")] [InlineData(true, false, "revoke")]
    public async Task Audit_HTTP_rechecks_the_original_human_after_query_before_returning_any_private_facts(bool sqlite, bool system, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> backing = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = backing.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.RemoveAll<IAuthorizationAuditQueryStore>(); s.AddSingleton(p => new AuditReadBarrier((IAuthorizationAuditQueryStore)p.GetRequiredService<IIamStore>()));
                s.AddSingleton<IAuthorizationAuditQueryStore>(p => p.GetRequiredService<AuditReadBarrier>());
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var actor = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("principalId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            HttpClient reader = owner; HttpClient? memberClient = null; var login = "owner@example.test"; var password = "explicit-synthetic-test-password"; Guid? assignmentId = null;
            try
            {
                if (mode == "revoke")
                {
                    var account = await identity.RegisterAsync("audit-exact-reader@example.test", "Audit reader", "strong-audit-reader-password"); login = account.LoginKey; password = "strong-audit-reader-password";
                    var role = await access.SaveRoleAsync(actor, tenant, null, "Only audit access", ["security.audit.read"], await Revision(owner, tenant));
                    var invite = await access.InviteAsync(actor, tenant, login, role.RoleId, await Revision(owner, tenant));
                    var user = await access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
                    assignmentId = (await security.ReadTenantAsync(tenant))!.Assignments.Single(a => a.PrincipalId == user.PrincipalId && a.RoleId == role.RoleId).AssignmentId;
                    reader = memberClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(reader, login, password);
                }
                var barrier = factory.Services.GetRequiredService<AuditReadBarrier>();
                var readerAccount = (await Json(await reader.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid();
                barrier.After = mode switch {
                    "logout" => async () => Assert.True((await reader.PostAsync("/api/v2/auth/logout", null)).IsSuccessStatusCode),
                    "password" => async () => await identity.ChangePasswordAsync(readerAccount, password, "new-strong-audit-reader-password"),
                    _ => async () => await access.RevokeAssignmentAsync(actor, tenant, assignmentId!.Value, await Revision(owner, tenant)) };
                var response = await reader.GetAsync(system ? "/api/v2/system/audit" : $"/api/v2/tenants/{tenant}/audit");
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("items", body); Assert.DoesNotContain("total", body);
            }
            finally { memberClient?.Dispose(); }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
