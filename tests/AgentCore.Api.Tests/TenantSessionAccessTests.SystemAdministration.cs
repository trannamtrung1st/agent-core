using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class SystemAdministrationBarrier(ISystemAdministrationStore inner) : ISystemAdministrationStore
    {
        public Func<Task>? BeforeWrite, AfterRead;
        private async Task Before() { if (Interlocked.Exchange(ref BeforeWrite, null) is { } action) await action(); }
        private async Task After() { if (Interlocked.Exchange(ref AfterRead, null) is { } action) await action(); }
        public async ValueTask<SystemAccountPage> QuerySystemAccountsAsync(SystemCollectionQuery query, CancellationToken ct = default)
        { var result = await inner.QuerySystemAccountsAsync(query, ct); await After(); return result; }
        public async ValueTask<SystemTenantPage> QuerySystemTenantsAsync(SystemCollectionQuery query, CancellationToken ct = default)
        { var result = await inner.QuerySystemTenantsAsync(query, ct); await After(); return result; }
        public async ValueTask<bool> TryCreateSystemTenantAsync(HumanSessionReference human, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
        { await Before(); return await inner.TryCreateSystemTenantAsync(human, tenant, audit, ct); }
        public async ValueTask<bool> TrySetSystemAccountStatusAsync(HumanSessionReference human, Guid accountId, string version, IdentityStatus status, string stamp, AuthorizationAudit audit, CancellationToken ct = default)
        { await Before(); return await inner.TrySetSystemAccountStatusAsync(human, accountId, version, status, stamp, audit, ct); }
        public async ValueTask<bool> TrySetSystemTenantStatusAsync(HumanSessionReference human, Guid tenantId, long revision, IdentityStatus status, AuthorizationAudit audit, CancellationToken ct = default)
        { await Before(); return await inner.TrySetSystemTenantStatusAsync(human, tenantId, revision, status, audit, ct); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_administration_HTTP_pages_only_metadata_and_atomically_changes_status_with_current_witnesses(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-administration-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var ownerAccount = (await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
            for (var i = 0; i < 3; i++) await identity.RegisterAsync($"system-index-{i}@example.test", "System fixture " + i, "strong-system-fixture-password");
            var accounts = await Json(await owner.GetAsync("/api/v2/system/accounts?q=System%20fixture&pageSize=1"));
            Assert.Equal(3, accounts.GetProperty("total").GetInt32()); var first = Assert.Single(accounts.GetProperty("items").EnumerateArray());
            var target = first.GetProperty("accountId").GetGuid(); var version = first.GetProperty("version").GetString()!; Assert.Equal(64, version.Length);
            Assert.DoesNotContain("passwordHash", accounts.GetRawText()); Assert.DoesNotContain("securityStamp", accounts.GetRawText()); Assert.DoesNotContain("principalId", accounts.GetRawText());
            var second = await Json(await owner.GetAsync("/api/v2/system/accounts?q=System%20fixture&pageSize=1&page=1")); Assert.NotEqual(target, Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("accountId").GetGuid());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = version, actorPrincipalId = Guid.NewGuid() })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = version })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Active", expectedVersion = version })).StatusCode);
            Assert.Equal(IdentityStatus.Suspended, (await security.ReadAccountAsync(target))!.Status);
            Assert.Equal(1, (await Json(await owner.GetAsync("/api/v2/system/accounts?status=Suspended"))).GetProperty("total").GetInt32());
            var updated = (await Json(await owner.GetAsync("/api/v2/system/accounts?status=Suspended"))).GetProperty("items")[0];
            Assert.NotEqual(version, updated.GetProperty("version").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Active", expectedVersion = updated.GetProperty("version").GetString() })).StatusCode);
            // Password-only changes also invalidate the opaque status witness, without exposing the stamp.
            var current = (await security.ReadAccountAsync(target))!; var beforePassword = SystemAdministrationRules.Version(current);
            await identity.ChangePasswordAsync(target, "strong-system-fixture-password", "strong-system-fixture-new-password");
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = beforePassword })).StatusCode);
            var selfVersion = SystemAdministrationRules.Version((await security.ReadAccountAsync(ownerAccount))!);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{ownerAccount}/status", new { status = "Suspended", expectedVersion = selfVersion })).StatusCode);
            Assert.Equal(IdentityStatus.Active, (await security.ReadAccountAsync(ownerAccount))!.Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Forged", slug = "forged", ownerAccountId = target })).StatusCode);
            Guid tenant = default;
            for (var i = 0; i < 3; i++) tenant = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "System organization " + i, slug = "system-organization-" + i }))).GetProperty("tenantId").GetGuid();
            var tenants = await Json(await owner.GetAsync("/api/v2/system/tenants?q=System%20organization&pageSize=1")); Assert.Equal(3, tenants.GetProperty("total").GetInt32());
            Assert.Single(tenants.GetProperty("items").EnumerateArray()); Assert.DoesNotContain("users", tenants.GetRawText()); Assert.DoesNotContain("assignments", tenants.GetRawText());
            var state = (await security.ReadTenantAsync(tenant))!; Assert.Equal(ownerAccount, Assert.Single(state.Users).AccountId);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var newOwner = await identity.RegisterAsync("separate-tenant-owner@example.test", "Separate organization owner", "strong-separate-owner-password");
            var newInvite = await access.InviteAsync(state.Users[0].PrincipalId, tenant, newOwner.LoginKey, state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
            await access.AcceptInvitationAsync(newOwner.AccountId, tenant, newInvite.Invitation.InvitationId, newInvite.Token);
            state = (await security.ReadTenantAsync(tenant))!;
            await access.SetMemberStatusAsync(state.Users.Single(u => u.AccountId == newOwner.AccountId).PrincipalId, tenant, state.Users.Single(u => u.AccountId == ownerAccount).UserId, IdentityStatus.Removed, state.Tenant.Revision);
            state = (await security.ReadTenantAsync(tenant))!;
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me")).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Suspended", expectedRevision = state.Tenant.Revision })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Active", expectedRevision = state.Tenant.Revision })).StatusCode);
            var suspended = (await security.ReadTenantAsync(tenant))!; Assert.Equal(state.Tenant.Revision + 1, suspended.Tenant.Revision); Assert.Equal(state.Users, suspended.Users);
            Assert.Equal(1, (await Json(await owner.GetAsync("/api/v2/system/tenants?status=Suspended"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Removed", expectedRevision = suspended.Tenant.Revision })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Active", expectedRevision = suspended.Tenant.Revision })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me")).StatusCode);
            foreach (var path in new[] { "/api/v2/system/accounts", "/api/v2/system/tenants" })
            {
                var response = await owner.GetAsync(path); Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?status=555")).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?q=" + new string('x', 129))).StatusCode);
            }
            var unprivileged = await identity.RegisterAsync("system-no-rights@example.test", "Tenant owner only", "strong-system-no-rights-password");
            var last = (await security.ReadTenantAsync(tenant))!;
            var invitation = await access.InviteAsync(last.Users.Single(u => u.AccountId == newOwner.AccountId).PrincipalId, tenant, unprivileged.LoginKey, last.Roles.Single(r => r.Key == "owner").RoleId, last.Tenant.Revision);
            await access.AcceptInvitationAsync(unprivileged.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var local = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(local, unprivileged.LoginKey, "strong-system-no-rights-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await local.GetAsync("/api/v2/system/accounts")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await local.GetAsync("/api/v2/system/tenants")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await local.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Denied", slug = "denied" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await local.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Suspended", expectedRevision = (await security.ReadTenantAsync(tenant))!.Tenant.Revision })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await local.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = beforePassword })).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false, "create", "logout")] [InlineData(true, "create", "logout")]
    [InlineData(false, "account", "logout")] [InlineData(true, "account", "logout")]
    [InlineData(false, "tenant", "logout")] [InlineData(true, "tenant", "logout")]
    [InlineData(false, "create", "password")] [InlineData(true, "create", "password")]
    [InlineData(false, "account", "password")] [InlineData(true, "account", "password")]
    [InlineData(false, "tenant", "password")] [InlineData(true, "tenant", "password")]
    public async Task System_administration_HTTP_rechecks_original_login_inside_each_commit_even_after_replacement_login(bool sqlite, string command, string retirement)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-administration-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> backing = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = backing.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.RemoveAll<ISystemAdministrationStore>(); s.AddSingleton(p => new SystemAdministrationBarrier((ISystemAdministrationStore)p.GetRequiredService<IIamStore>()));
                s.AddSingleton<ISystemAdministrationStore>(p => p.GetRequiredService<SystemAdministrationBarrier>());
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
            var ownerAccount = (await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid();
            var target = await identity.RegisterAsync("system-fence-target@example.test", "Status target", "strong-system-target-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var state = (await security.ReadTenantAsync(tenant))!;
            var barrier = factory.Services.GetRequiredService<SystemAdministrationBarrier>();
            barrier.BeforeWrite = async () => {
                if (retirement == "logout") Assert.True((await owner.PostAsync("/api/v2/auth/logout", null)).IsSuccessStatusCode);
                else await identity.ChangePasswordAsync(ownerAccount, "explicit-synthetic-test-password", "strong-system-owner-new-password");
                await Login(owner, "owner@example.test", retirement == "logout" ? "explicit-synthetic-test-password" : "strong-system-owner-new-password");
            };
            var response = command switch {
                "create" => await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Must not exist", slug = "must-not-exist" }),
                "account" => await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target.AccountId}/status", new { status = "Suspended", expectedVersion = SystemAdministrationRules.Version(target) }),
                _ => await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Suspended", expectedRevision = state.Tenant.Revision }) };
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Contains("initiator_unavailable", await response.Content.ReadAsStringAsync());
            Assert.Equal(IdentityStatus.Active, (await security.ReadAccountAsync(target.AccountId))!.Status); Assert.Equal(target.SecurityStamp, (await security.ReadAccountAsync(target.AccountId))!.SecurityStamp);
            Assert.Equal(state.Tenant, (await security.ReadTenantAsync(tenant))!.Tenant);
            Assert.Empty((await ((ISystemAdministrationStore)security).QuerySystemTenantsAsync(new(Search: "Must not exist"))).Items);
            var audits = (await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null, new(Action: "system.accounts.status"))).Items; Assert.Empty(audits);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task System_administration_HTTP_rechecks_original_login_after_catalogue_read(bool sqlite, bool accounts)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-administration-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> backing = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = backing.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.RemoveAll<ISystemAdministrationStore>(); s.AddSingleton(p => new SystemAdministrationBarrier((ISystemAdministrationStore)p.GetRequiredService<IIamStore>())); s.AddSingleton<ISystemAdministrationStore>(p => p.GetRequiredService<SystemAdministrationBarrier>());
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            factory.Services.GetRequiredService<SystemAdministrationBarrier>().AfterRead = async () => { Assert.True((await owner.PostAsync("/api/v2/auth/logout", null)).IsSuccessStatusCode); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); };
            var response = await owner.GetAsync(accounts ? "/api/v2/system/accounts" : "/api/v2/system/tenants"); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("items", body); Assert.DoesNotContain("total", body);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_administration_atomic_store_refuses_forged_humans_and_audit_and_serializes_one_account_witness(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-administration-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var client = factory.CreateClient();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
            var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var actor = (await security.ReadSystemAsync()).Principals.Single(p => p.AccountId == login.Account.AccountId);
            var human = new HumanSessionReference(login.Account.AccountId, login.Session.SessionId, actor.PrincipalId);
            var target = await identity.RegisterAsync("atomic-system-target@example.test", "Atomic status target", "strong-atomic-target-password");
            var tenant = (await security.ListMembershipsAsync(login.Account.AccountId)).Single(); var state = (await security.ReadTenantAsync(tenant.Tenant.TenantId))!;
            var store = (ISystemAdministrationStore)security; var version = SystemAdministrationRules.Version(target);
            AuthorizationAudit Fact(HumanSessionReference h) => new(Guid.NewGuid(), null, h.PrincipalId, "system.accounts.status", target.AccountId.ToString("D"), "allowed", "authorized", DateTimeOffset.UtcNow);
            var forged = human with { PrincipalId = tenant.User.PrincipalId };
            await Assert.ThrowsAsync<AccessDeniedException>(() => store.TrySetSystemAccountStatusAsync(forged, target.AccountId, version, IdentityStatus.Suspended, "fresh-stamp", Fact(forged)).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.TrySetSystemAccountStatusAsync(human, target.AccountId, version, IdentityStatus.Suspended, "fresh-stamp", Fact(human) with { Target = "private forged target" }).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.TrySetSystemAccountStatusAsync(human, target.AccountId, version, IdentityStatus.Suspended, target.SecurityStamp, Fact(human)).AsTask());
            var retained = (await security.ReadAccountAsync(target.AccountId))!; Assert.Equal(target.CreatedAt.ToUnixTimeMilliseconds(), retained.CreatedAt.ToUnixTimeMilliseconds()); Assert.Equal(target with { CreatedAt = retained.CreatedAt }, retained);
            var forgedTenant = state with { Users = state.Users.Select(u => u with { AccountId = target.AccountId }).ToArray() };
            var creation = new AuthorizationAudit(Guid.NewGuid(), state.Tenant.TenantId, human.PrincipalId, "system.tenants.create", state.Tenant.TenantId.ToString("D"), "allowed", "authorized", DateTimeOffset.UtcNow);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.TryCreateSystemTenantAsync(human, forgedTenant, creation).AsTask());
            var results = await Task.WhenAll(store.TrySetSystemAccountStatusAsync(human, target.AccountId, version, IdentityStatus.Suspended, "fresh-first-stamp", Fact(human)).AsTask(),
                store.TrySetSystemAccountStatusAsync(human, target.AccountId, version, IdentityStatus.Removed, "fresh-second-stamp", Fact(human)).AsTask());
            Assert.Single(results, result => result); var current = (await security.ReadAccountAsync(target.AccountId))!;
            Assert.NotEqual(target.SecurityStamp, current.SecurityStamp); Assert.Equal(target.PasswordHash, current.PasswordHash); Assert.Equal(target.LoginKey, current.LoginKey);
            Assert.Single((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null, new(Action: "system.accounts.status"))).Items);
            Assert.Null(await identity.SignInAsync(target.LoginKey, "strong-atomic-target-password"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task System_administration_SQLite_rolls_back_status_and_audit_then_reopens_current_metadata_for_explicit_recovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "system-administration-reopen-" + Guid.NewGuid().ToString("N"));
        Guid target = default, tenant = default; long revision = 0;
        try
        {
            await using (var factory = new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()))
            {
                using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
                var account = await identity.RegisterAsync("durable-system-status@example.test", "Durable system target", "strong-durable-system-password"); target = account.AccountId;
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); revision = (await security.ReadTenantAsync(tenant))!.Tenant.Revision;
                var contexts = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
                await using (var db = await contexts.CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_system_status_audit BEFORE INSERT ON AuthorizationAudit WHEN NEW.Action IN ('system.accounts.status','system.tenants.status') BEGIN SELECT RAISE(ABORT,'controlled System audit fault'); END;");
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = SystemAdministrationRules.Version(account) })).StatusCode);
                var retained = (await security.ReadAccountAsync(target))!; Assert.Equal(account.CreatedAt.ToUnixTimeMilliseconds(), retained.CreatedAt.ToUnixTimeMilliseconds()); Assert.Equal(account with { CreatedAt = retained.CreatedAt }, retained);
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Suspended", expectedRevision = revision })).StatusCode);
                Assert.Equal(IdentityStatus.Active, (await security.ReadTenantAsync(tenant))!.Tenant.Status); Assert.Equal(revision, (await security.ReadTenantAsync(tenant))!.Tenant.Revision);
                await using (var db = await contexts.CreateDbContextAsync()) { Assert.Equal(0, await db.Set<AuthorizationAudit>().CountAsync(a => a.Action == "system.accounts.status" || a.Action == "system.tenants.status")); await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_system_status_audit;"); }
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Suspended", expectedVersion = SystemAdministrationRules.Version(account) })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Suspended", expectedRevision = revision })).StatusCode);
            }
            await using (var reopened = new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()))
            {
                using var owner = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var accounts = await Json(await owner.GetAsync("/api/v2/system/accounts?status=Suspended")); var account = Assert.Single(accounts.GetProperty("items").EnumerateArray()); Assert.Equal(target, account.GetProperty("accountId").GetGuid());
                var tenants = await Json(await owner.GetAsync("/api/v2/system/tenants?status=Suspended")); var organization = Assert.Single(tenants.GetProperty("items").EnumerateArray()); Assert.Equal(tenant, organization.GetProperty("tenantId").GetGuid()); Assert.Equal(revision + 1, organization.GetProperty("revision").GetInt64());
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/tenants/{tenant}/status", new { status = "Active", expectedRevision = revision + 1 })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/accounts/{target}/status", new { status = "Active", expectedVersion = account.GetProperty("version").GetString() })).StatusCode);
                await using var db = await reopened.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await db.Set<AuthorizationAudit>().CountAsync(a => a.Action == "system.accounts.status")); Assert.Equal(2, await db.Set<AuthorizationAudit>().CountAsync(a => a.Action == "system.tenants.status"));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, "Suspended")] [InlineData(true, "Suspended")] [InlineData(false, "Removed")] [InlineData(true, "Removed")]
    public async Task System_administration_HTTP_preserves_last_platform_operator_after_transferring_all_tenant_ownership(bool sqlite, string status)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-operator-floor-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var id = (await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid(); var original = (await security.ReadAccountAsync(id))!;
            var member = await identity.RegisterAsync("replacement-tenant-owner@example.test", "Replacement tenant Owner", "strong-replacement-owner-password");
            var membership = Assert.Single(await security.ListMembershipsAsync(id)); var tenant = membership.Tenant.TenantId; var state = (await security.ReadTenantAsync(tenant))!;
            var invite = await access.InviteAsync(membership.User.PrincipalId, tenant, member.LoginKey, state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
            var newOwner = await access.AcceptInvitationAsync(member.AccountId, tenant, invite.Invitation.InvitationId, invite.Token); state = (await security.ReadTenantAsync(tenant))!;
            await access.SetMemberStatusAsync(newOwner.PrincipalId, tenant, membership.User.UserId, IdentityStatus.Removed, state.Tenant.Revision);
            Assert.Empty(await security.ListMembershipsAsync(id));
            var response = await owner.PutAsJsonAsync($"/api/v2/system/accounts/{id}/status", new { status, expectedVersion = SystemAdministrationRules.Version(original) });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("last_system_operator", await response.Content.ReadAsStringAsync());
            Assert.Equal(original, await security.ReadAccountAsync(id)); Assert.NotNull(await identity.SignInAsync(original.LoginKey, "explicit-synthetic-test-password"));
            Assert.Empty((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null, new(Action: "system.accounts.status"))).Items);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
