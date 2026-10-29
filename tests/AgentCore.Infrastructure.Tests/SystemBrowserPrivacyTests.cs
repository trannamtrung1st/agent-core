using System.Reflection;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SystemBrowserPrivacyTests
{
    private sealed class Contexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
    private sealed class BeforeStore(ISystemBrowserPrivacyStore inner, Func<Task> before) : ISystemBrowserPrivacyStore
    {
        public async ValueTask<bool> TrySaveSystemBrowserPrivacyAsync(HumanSessionReference human, SystemBrowserPrivacyChange change, AuthorizationAudit audit, CancellationToken ct = default)
        { await before(); return await inner.TrySaveSystemBrowserPrivacyAsync(human, change, audit, ct); }
    }
    private static async Task With(bool sqlite, Func<BrowserPrivacyTestHost, IDbContextFactory<AgentCoreDbContext>?, Task> action, bool historicalRoundtrip = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "system-privacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            IDbContextFactory<AgentCoreDbContext>? contexts = sqlite ? new Contexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db;Foreign Keys=True;Default Timeout=10").Options) : null;
            if (contexts is not null)
            {
                await using var db = await contexts.CreateDbContextAsync();
                if (historicalRoundtrip)
                {
                    var migrator = db.GetService<IMigrator>();
                    await migrator.MigrateAsync("20261020080000_SystemBrowserPrivacyAuthority");
                    await migrator.MigrateAsync("20261020060000_TenantBrowserProfileReset");
                }
                await db.Database.MigrateAsync();
            }
            await action(await BrowserPrivacyTestHost.Create(new(true, true, ["https://allowed.test"], ["https://graphics.test"]), contexts), contexts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static AuthorizationAudit Audit(BrowserPrivacyTestHost f) => new(f.Ids.NewId(), null, f.Human.PrincipalId, "system.browser.privacy.update", "host", "allowed", "policy_saved_restart_required", DateTimeOffset.UtcNow);
    private static async Task<IReadOnlyList<AuthorizationAudit>> Audits(BrowserPrivacyTestHost f, IDbContextFactory<AgentCoreDbContext>? contexts)
    {
        if (contexts is not null) { await using var db = await contexts.CreateDbContextAsync(); return await db.Set<AuthorizationAudit>().Where(a => a.Action == "system.browser.privacy.update").ToArrayAsync(); }
        // Inspect the actual incumbent in-memory audit, without adding a product debug/read authority path.
        return ((List<AuthorizationAudit>)f.Iam.GetType().GetField("_audit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Iam)!).Where(a => a.Action == "system.browser.privacy.update").ToArray();
    }
    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")] [InlineData(false, "password")] [InlineData(true, "password")]
    public async Task System_privacy_rechecks_original_human_inside_the_write_transaction(bool sqlite, string boundary) => await With(sqlite, async (f, contexts) => {
        var store = new BeforeStore((ISystemBrowserPrivacyStore)f.Iam, async () => { if (boundary == "logout") await f.Identity.SignOutAsync(f.Human.AuthenticationSessionId); else await f.Identity.ChangePasswordAsync(f.Human.AccountId, "strong-privacy-password", "strong-rotated-privacy-password"); });
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service(store).SaveAsync(f.Human, new(0, "Disabled", [], [], false)).AsTask());
        Assert.Empty(await Audits(f, contexts)); Assert.Equal(0, (await f.Runtime.ReadAsync()).Saved.Revision);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_privacy_store_requires_actual_system_human_raw_permission_and_deployment_ceiling(bool sqlite) => await With(sqlite, async (f, contexts) => {
        var store = (ISystemBrowserPrivacyStore)f.Iam; var change = new SystemBrowserPrivacyChange(0, "Disabled", [], [], false);
        var state = (await f.Iam.ListMembershipsAsync(f.Human.AccountId)).Single();
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TrySaveSystemBrowserPrivacyAsync(f.Human with { PrincipalId = state.User.PrincipalId }, change, Audit(f) with { ActorPrincipalId = state.User.PrincipalId }).AsTask());
        var account = await f.Identity.RegisterAsync("ordinary@example.test", "Ordinary Account", "strong-ordinary-password"); var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-ordinary-password"))!;
        var principal = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == account.AccountId);
        var deniedHuman = new HumanSessionReference(account.AccountId, login.Session.SessionId, principal.PrincipalId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TrySaveSystemBrowserPrivacyAsync(deniedHuman, change, Audit(f) with { ActorPrincipalId = principal.PrincipalId }).AsTask());
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.TrySaveSystemBrowserPrivacyAsync(f.Human, new(0, "Unmasked", ["https://outside.test"], [], true), Audit(f)).AsTask());
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.TrySaveSystemBrowserPrivacyAsync(f.Human, new(0, "Unmasked", ["https://allowed.test"], [], false), Audit(f)).AsTask());
        Assert.Empty(await Audits(f, contexts)); Assert.Equal(0, (await f.Runtime.ReadAsync()).Saved.Revision);
    });
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task System_privacy_CAS_has_one_winner_with_atomic_body_free_global_audit_and_pinned_activation(bool sqlite) => await With(sqlite, async (f, contexts) => {
        async Task<bool> Save() { try { await f.SaveAsync(0, "Unmasked", ["https://allowed.test"], ["https://graphics.test"], true); return true; } catch (SecurityConflictException) { return false; } }
        var results = await Task.WhenAll(Task.Run(Save), Task.Run(Save)); Assert.Single(results, x => x);
        var saved = await f.ReadAsync(); Assert.Equal(1, saved.Saved.Revision); Assert.Equal(BrowserScreenshotPrivacyMode.Protected, saved.Effective.Mode); Assert.True(saved.RestartRequired);
        var audit = Assert.Single(await Audits(f, contexts)); Assert.Null(audit.TenantId); Assert.Equal(f.Human.PrincipalId, audit.ActorPrincipalId);
        Assert.DoesNotContain("allowed.test", JsonSerializer.Serialize(audit)); Assert.DoesNotContain("graphics.test", JsonSerializer.Serialize(audit));
        await f.ActivateAtStartupAsync(); Assert.Equal(BrowserScreenshotPrivacyMode.Unmasked, f.Effective.Mode);
    });
    [Fact]
    public async Task System_privacy_SQLite_shape_guards_and_downgrade_preserve_canonical_policy_and_audit() => await With(true, async (f, contexts) => {
        await using var db = await contexts!.CreateDbContextAsync(); Assert.False(db.Database.HasPendingModelChanges());
        await f.SaveAsync(0, "Unmasked", ["https://allowed.test"], [], true);
        foreach (var sql in new[] { "UPDATE BrowserPrivacy SET Revision=2,PolicyJson=json_set(PolicyJson,'$.Mode',9,'$.Revision',2)", "UPDATE BrowserPrivacy SET Revision=2,PolicyJson=json_set(PolicyJson,'$.Revision',2,'$.Extra','forged')", "UPDATE BrowserPrivacy SET Revision=2,PolicyJson=json_set(PolicyJson,'$.Revision',2,'$.UnmaskedOrigins[0]','https://allowed.test/private')", "DELETE FROM BrowserPrivacy" })
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
        await using (var historical = await HistoricalTenantDatabase.CreateAsync(contexts,
            "20261020080000_SystemBrowserPrivacyAuthority", ["BrowserPrivacy"]))
        {
            var before = (await historical.Db.Database.GetAppliedMigrationsAsync()).ToArray();
            var refusal = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                historical.Db.GetService<IMigrator>().MigrateAsync("20261020060000_TenantBrowserProfileReset"));
            Assert.Contains("OwnedRows=0", refusal.Message);
            Assert.Equal(before, await historical.Db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await historical.Db.Database.SqlQueryRaw<int>("SELECT Revision AS Value FROM BrowserPrivacy").SingleAsync());
        }
        Assert.Equal(1, (await f.ReadAsync()).Saved.Revision); Assert.Single(await Audits(f, contexts)); await db.Database.MigrateAsync();
        var reopened = await BrowserPrivacyTestHost.Create(new(true, false, [], []), contexts); await reopened.ActivateAtStartupAsync(); Assert.Equal(BrowserScreenshotPrivacyMode.Protected, reopened.Effective.Mode);
        Assert.Equal(1, (await reopened.ReadAsync()).Saved.Revision); Assert.True((await reopened.ReadAsync()).ConstrainedByDeployment);
    }, historicalRoundtrip: true);
}
