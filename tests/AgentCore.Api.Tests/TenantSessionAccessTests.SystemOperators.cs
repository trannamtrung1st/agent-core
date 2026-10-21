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
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class SystemOperatorBarrier(ISystemOperatorStore inner) : ISystemOperatorStore
    {
        public Func<Task>? BeforeWrite, AfterRead;
        public async ValueTask<SystemOperatorPage> QuerySystemOperatorsAsync(SystemCollectionQuery query, bool? isOperator, CancellationToken ct = default)
        { var result = await inner.QuerySystemOperatorsAsync(query, isOperator, ct); if (Interlocked.Exchange(ref AfterRead, null) is { } action) await action(); return result; }
        public async ValueTask<bool> TrySetSystemOperatorAsync(HumanSessionReference human, Guid id, string version, bool enabled, Guid assignment, string stamp, AuthorizationAudit audit, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref BeforeWrite, null) is { } action) await action(); return await inner.TrySetSystemOperatorAsync(human, id, version, enabled, assignment, stamp, audit, ct); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_operator_HTTP_assign_revoke_uses_exact_current_role_and_invalidates_old_logins_without_tenant_access(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-operators-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var security = factory.Services.GetRequiredService<IIamStore>(); var identity = factory.Services.GetRequiredService<IdentityService>();
            var me = await Json(await owner.GetAsync("/api/v2/account/me")); var ownerId = me.GetProperty("account").GetProperty("accountId").GetGuid();
            var target = await identity.RegisterAsync("operator-target@example.test", "Platform candidate", "strong-operator-target-password");
            using var candidate = factory.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(candidate, target.LoginKey, "strong-operator-target-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await candidate.GetAsync("/api/v2/system/operators")).StatusCode);
            var page = await Json(await owner.GetAsync("/api/v2/system/operators?q=Platform%20candidate&isOperator=false&pageSize=1"));
            Assert.Equal(1, page.GetProperty("total").GetInt32()); var row = Assert.Single(page.GetProperty("items").EnumerateArray()); var version = row.GetProperty("operatorVersion").GetString();
            Assert.False(row.GetProperty("isOperator").GetBoolean()); Assert.DoesNotContain("securityStamp", page.GetRawText()); Assert.DoesNotContain("password", page.GetRawText()); Assert.DoesNotContain("principalId", page.GetRawText());
            foreach (var body in new object[] { new { enabled = true }, new { expectedVersion = version }, new { enabled = true, expectedVersion = version, roleId = Guid.NewGuid() } })
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", body)).StatusCode);
            var tenantIds = await security.ListMembershipsAsync(ownerId); var tenant = Assert.Single(tenantIds).Tenant.TenantId;
            var tenantBefore = (await security.ReadTenantAsync(tenant))!;
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", new { enabled = true, expectedVersion = version })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await candidate.GetAsync("/api/v2/account/me")).StatusCode);
            await Login(candidate, target.LoginKey, "strong-operator-target-password");
            Assert.Equal(HttpStatusCode.OK, (await candidate.GetAsync("/api/v2/system/accounts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await candidate.GetAsync($"/api/v2/tenants/{tenant}/access/me")).StatusCode);
            var tenantAfter = (await security.ReadTenantAsync(tenant))!;
            Assert.Equal(tenantBefore.Tenant, tenantAfter.Tenant); Assert.Equal(tenantBefore.Users, tenantAfter.Users); Assert.Equal(tenantBefore.Assignments, tenantAfter.Assignments);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", new { enabled = false, expectedVersion = version })).StatusCode);
            var active = await Json(await owner.GetAsync("/api/v2/system/operators?isOperator=true")); Assert.Equal(2, active.GetProperty("total").GetInt32());
            row = active.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("accountId").GetGuid() == target.AccountId);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", new { enabled = false, expectedVersion = row.GetProperty("operatorVersion").GetString() })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await candidate.GetAsync("/api/v2/system/operators")).StatusCode); await Login(candidate, target.LoginKey, "strong-operator-target-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await candidate.GetAsync("/api/v2/system/operators")).StatusCode);
            // The old no-role witness cannot survive the grant/revoke ABA sequence.
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", new { enabled = true, expectedVersion = version })).StatusCode);
            var only = Assert.Single((await Json(await owner.GetAsync("/api/v2/system/operators?isOperator=true"))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v2/system/operators/{ownerId}", new { enabled = false, expectedVersion = only.GetProperty("operatorVersion").GetString() })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v2/system/operators")).StatusCode);
            var audits = await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null, new(Action: "system.operators.assign")); Assert.Single(audits.Items);
            Assert.Single((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null, new(Action: "system.operators.revoke"))).Items);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task System_operator_HTTP_original_login_is_rechecked_at_commit_and_after_query(bool sqlite, bool read)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-operator-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> source = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            SystemOperatorBarrier? barrier = null;
            await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                services.RemoveAll<ISystemOperatorStore>(); services.AddSingleton<ISystemOperatorStore>(p => barrier = new((ISystemOperatorStore)p.GetRequiredService<IIamStore>())); }));
            using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
            var target = await identity.RegisterAsync("operator-fence@example.test", "Fence candidate", "strong-operator-fence-password");
            var row = Assert.Single((await Json(await owner.GetAsync("/api/v2/system/operators?q=Fence%20candidate"))).GetProperty("items").EnumerateArray());
            async Task Retire() { await owner.PostAsync("/api/v2/auth/logout", null); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); }
            if (read) barrier!.AfterRead = Retire; else barrier!.BeforeWrite = Retire;
            var response = read ? await owner.GetAsync("/api/v2/system/operators") : await owner.PutAsJsonAsync($"/api/v2/system/operators/{target.AccountId}", new { enabled = true, expectedVersion = row.GetProperty("operatorVersion").GetString() });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Contains("initiator_unavailable", await response.Content.ReadAsStringAsync());
            Assert.Empty(SystemOperatorRules.Assignments(await security.ReadSystemAsync(), target)); Assert.Equal(target.SecurityStamp, (await security.ReadAccountAsync(target.AccountId))!.SecurityStamp);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_operator_actual_store_rejects_forgery_inactive_grants_and_concurrent_stale_writes(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-operator-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var identity = factory.Services.GetRequiredService<IdentityService>(); var security = factory.Services.GetRequiredService<IIamStore>();
            var target = await identity.RegisterAsync("atomic-operator@example.test", "Atomic candidate", "strong-atomic-operator-password");
            var signed = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var system = await security.ReadSystemAsync(); var principal = system.Principals.Single(p => p.AccountId == signed.Account.AccountId);
            var human = new HumanSessionReference(signed.Account.AccountId, signed.Session.SessionId, principal.PrincipalId); var store = (ISystemOperatorStore)security;
            var version = SystemOperatorRules.Version(system, target);
            AuthorizationAudit Audit() => new(Guid.NewGuid(), null, human.PrincipalId, "system.operators.assign", target.AccountId.ToString("D"), "allowed", "authorized", DateTimeOffset.UtcNow);
            var forged = human with { AuthenticationSessionId = Guid.NewGuid() };
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await store.TrySetSystemOperatorAsync(forged, target.AccountId, version, true, Guid.NewGuid(), "new-stamp", Audit()));
            var tenant = Assert.Single(await security.ListMembershipsAsync(human.AccountId));
            var tenantHuman = human with { PrincipalId = tenant.User.PrincipalId };
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await store.TrySetSystemOperatorAsync(tenantHuman, target.AccountId, version, true, Guid.NewGuid(), "new-stamp", Audit() with { ActorPrincipalId = tenantHuman.PrincipalId }));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.TrySetSystemOperatorAsync(human, target.AccountId, version, true, Guid.NewGuid(), target.SecurityStamp, Audit()));
            var tasks = Enumerable.Range(0,2).Select(async i => await store.TrySetSystemOperatorAsync(human,target.AccountId,version,true,Guid.NewGuid(),"concurrent-stamp-"+i,Audit())).ToArray();
            var results = await Task.WhenAll(tasks); Assert.Single(results, value => value);
            var current = (await security.ReadAccountAsync(target.AccountId))!; var updated = await security.ReadSystemAsync();
            Assert.Single(SystemOperatorRules.Assignments(updated,current)); Assert.NotEqual(version,SystemOperatorRules.Version(updated,current));
            Assert.Single((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null,new(Action:"system.operators.assign"))).Items);
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await store.TrySetSystemOperatorAsync(human,target.AccountId,SystemOperatorRules.Version(updated,current),true,Guid.NewGuid(),"unchanged-new-stamp",Audit()));
            Assert.Equal(current.SecurityStamp,(await security.ReadAccountAsync(target.AccountId))!.SecurityStamp);
            var suspended = await identity.RegisterAsync("suspended-operator@example.test", "Inactive candidate", "strong-inactive-operator-password");
            var admin = factory.Services.GetRequiredService<SystemAdministrationService>(); await admin.SetAccountStatusAsync(human,suspended.AccountId,SystemAdministrationRules.Version(suspended),IdentityStatus.Suspended,CancellationToken.None);
            suspended = (await security.ReadAccountAsync(suspended.AccountId))!;
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await store.TrySetSystemOperatorAsync(human,suspended.AccountId,SystemOperatorRules.Version(await security.ReadSystemAsync(),suspended),true,Guid.NewGuid(),"inactive-stamp",Audit() with { Target = suspended.AccountId.ToString("D") }));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Fact]
    public async Task System_operator_SQLite_audit_failure_rolls_back_role_and_stamp_and_assignment_survives_actual_host_recreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "system-operator-reopen-" + Guid.NewGuid().ToString("N")); Guid target = default;
        try
        {
            await using (var factory = new TenantApprovalSqliteFactory(root,new ApprovalRecordingModel()))
            {
                using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(owner,"owner@example.test","explicit-synthetic-test-password");
                var security = factory.Services.GetRequiredService<IIamStore>(); var identity = factory.Services.GetRequiredService<IdentityService>();
                var account = await identity.RegisterAsync("reopen-operator@example.test","Reopen candidate","strong-reopen-operator-password"); target = account.AccountId;
                var row = Assert.Single((await Json(await owner.GetAsync("/api/v2/system/operators?q=Reopen%20candidate"))).GetProperty("items").EnumerateArray()); var version = row.GetProperty("operatorVersion").GetString();
                var contexts = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
                await using (var db = await contexts.CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_operator_audit BEFORE INSERT ON AuthorizationAudit WHEN NEW.Action = 'system.operators.assign' BEGIN SELECT RAISE(ABORT,'controlled operator audit fault'); END;");
                Assert.Equal(HttpStatusCode.Conflict,(await owner.PutAsJsonAsync($"/api/v2/system/operators/{target}",new { enabled=true,expectedVersion=version })).StatusCode);
                Assert.Equal(account.SecurityStamp,(await security.ReadAccountAsync(target))!.SecurityStamp); Assert.Empty(SystemOperatorRules.Assignments(await security.ReadSystemAsync(),account));
                await using (var db = await contexts.CreateDbContextAsync()) { Assert.Equal(0,await db.Set<AuthorizationAudit>().CountAsync(a=>a.Action=="system.operators.assign")); await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_operator_audit;"); }
                Assert.Equal(HttpStatusCode.NoContent,(await owner.PutAsJsonAsync($"/api/v2/system/operators/{target}",new { enabled=true,expectedVersion=version })).StatusCode);
            }
            await using (var reopened = new TenantApprovalSqliteFactory(root,new ApprovalRecordingModel()))
            {
                using var candidate = reopened.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(candidate,"reopen-operator@example.test","strong-reopen-operator-password");
                var operators = await Json(await candidate.GetAsync("/api/v2/system/operators?isOperator=true")); Assert.Equal(2,operators.GetProperty("total").GetInt32());
                var row = operators.GetProperty("items").EnumerateArray().Single(r=>r.GetProperty("accountId").GetGuid()==target);
                Assert.Equal(HttpStatusCode.NoContent,(await candidate.PutAsJsonAsync($"/api/v2/system/operators/{target}",new { enabled=false,expectedVersion=row.GetProperty("operatorVersion").GetString() })).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized,(await candidate.GetAsync("/api/v2/system/operators")).StatusCode);
            }
            await using (var final = new TenantApprovalSqliteFactory(root,new ApprovalRecordingModel()))
            {
                using var candidate = final.CreateClient(new() { BaseAddress = new("https://localhost") }); await Login(candidate,"reopen-operator@example.test","strong-reopen-operator-password"); Assert.Equal(HttpStatusCode.Forbidden,(await candidate.GetAsync("/api/v2/system/operators")).StatusCode);
                var security = final.Services.GetRequiredService<IIamStore>(); Assert.Empty(SystemOperatorRules.Assignments(await security.ReadSystemAsync(),(await security.ReadAccountAsync(target))!));
                Assert.Single((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null,new(Action:"system.operators.assign"))).Items); Assert.Single((await ((IAuthorizationAuditQueryStore)security).QueryAuditAsync(null,new(Action:"system.operators.revoke"))).Items);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

}
