using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Email;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class EmailWatchStoreTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Configuration_claims_cursor_reopen_and_revocation_are_independent_of_credentials(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"email-watch-{Guid.NewGuid():N}.db");
        var factory = new Factory(path); var instances = new InMemoryAgentInstanceStore();
        ICredentialStore credentials = sqlite ? new SqliteCredentialStore(factory) : new InMemoryCredentialStore(instances);
        IEmailWatchStore watches = sqlite ? new SqliteEmailWatchStore(factory) : new InMemoryEmailWatchStore(instances, credentials, (IAgentCredentialBindingStore)credentials, new());
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        var c = new Credential(id, "Watch", CredentialKind.Password, CredentialStatus.Active, new Dictionary<string, string>(), [], "protected", 1, 1, now, now, CredentialPurpose.EmailAccount, EmailAccountProfile.Custom("test@example.test"));
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); }
            await credentials.SaveAsync(c, 0);
            Assert.False((await watches.GetAsync(id)).Enabled); Assert.Empty(await watches.ClaimDueAsync(now, 8));
            var config = await watches.ConfigureAsync(id, 0, true, 60, now);
            Assert.Equal(1, config.ConfigurationRevision); Assert.Equal(1, (await credentials.GetAsync(id))!.Revision);
            await Assert.ThrowsAsync<AgentCoreException>(() => watches.ConfigureAsync(id, 0, false, 300, now).AsTask());
            var first = Assert.Single(await watches.ClaimDueAsync(now, 8)); Assert.Null(first.State.Cursor);
            Assert.Empty(await watches.ClaimDueAsync(now, 8));
            var expired = Assert.Single(await watches.ClaimDueAsync(now.AddSeconds(91), 8));
            Assert.False(await watches.CommitAsync(first, new([], new(EmailWatchCursorKind.ImapUid, UidValidity: 1, LastUid: 100)), [], now.AddSeconds(92)));
            var cursor = new EmailWatchCursor(EmailWatchCursorKind.ImapUid, UidValidity: 1, LastUid: 100);
            Assert.True(await watches.CommitAsync(expired, new([], cursor), [], now.AddSeconds(92)));
            if (sqlite) watches = new SqliteEmailWatchStore(factory);
            Assert.Equal(cursor, (await watches.GetAsync(id)).Cursor);
            config = await watches.ConfigureAsync(id, 1, true, 900, now.AddSeconds(93)); Assert.Equal(cursor, config.Cursor);
            await watches.CheckNowAsync(id, 2, now.AddSeconds(94));
            var running = Assert.Single(await watches.ClaimDueAsync(now.AddSeconds(94), 8));
            config = await watches.ConfigureAsync(id, 2, false, 300, now.AddSeconds(95));
            Assert.False(await watches.CommitAsync(running, new([], cursor), [], now.AddSeconds(96)));
            Assert.Empty(await watches.ClaimDueAsync(now.AddHours(1), 8));
            config = await watches.ConfigureAsync(id, 3, true, 300, now.AddSeconds(97)); Assert.Null(config.Cursor); Assert.Equal(now.AddSeconds(97), config.LastGapAtUtc);
            var revokedClaim = Assert.Single(await watches.ClaimDueAsync(now.AddSeconds(97), 8));
            await credentials.SaveAsync(c with { Status = CredentialStatus.Disabled, Revision = 2 }, 1);
            await credentials.SaveAsync(c with { Revision = 3 }, 2);
            Assert.False(await watches.CommitAsync(revokedClaim, new([], cursor), [], now.AddSeconds(98)));
            await credentials.SaveAsync(c with { Status = CredentialStatus.Disabled, Revision = 4 }, 3);
            Assert.Empty(await watches.ClaimDueAsync(now.AddSeconds(98), 8));
            await Assert.ThrowsAsync<AgentCoreException>(() => watches.CheckNowAsync(id, 4, now.AddSeconds(99)).AsTask());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Leased_accounts_do_not_starve_other_due_accounts(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-fairness-{Guid.NewGuid():N}.db");
        var factory = new Factory(path); var instances = new InMemoryAgentInstanceStore();
        ICredentialStore credentials = sqlite ? new SqliteCredentialStore(factory) : new InMemoryCredentialStore(instances);
        IEmailWatchStore watches = sqlite ? new SqliteEmailWatchStore(factory) : new InMemoryEmailWatchStore(instances, credentials, (IAgentCredentialBindingStore)credentials, new());
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); }
            for (var i = 0; i < 2; i++)
            {
                var id = Guid.NewGuid(); await credentials.SaveAsync(new(id, "Due account", CredentialKind.Password, CredentialStatus.Active,
                    new Dictionary<string, string>(), [], "protected", 1, 1, now, now, CredentialPurpose.EmailAccount, EmailAccountProfile.Custom("fair@example.test")), 0);
                await watches.ConfigureAsync(id, 0, true, 60, now);
            }
            var first = Assert.Single(await watches.ClaimDueAsync(now, 1));
            var second = Assert.Single(await watches.ClaimDueAsync(now, 1));
            Assert.NotEqual(first.State.AccountId, second.State.AccountId); Assert.Empty(await watches.ClaimDueAsync(now, 1));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Paused_recovery_request_survives_reopen_and_expired_claim_without_releasing_fence(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-paused-recovery-{Guid.NewGuid():N}.db");
        var factory = new Factory(path); var instances = new InMemoryAgentInstanceStore();
        ICredentialStore credentials = sqlite ? new SqliteCredentialStore(factory) : new InMemoryCredentialStore(instances);
        IEmailWatchStore watches = sqlite ? new SqliteEmailWatchStore(factory) : new InMemoryEmailWatchStore(instances, credentials, (IAgentCredentialBindingStore)credentials, new());
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); }
            await credentials.SaveAsync(new(id, "Recovery", CredentialKind.Password, CredentialStatus.Active,
                new Dictionary<string, string>(), [], "protected", 1, 1, now, now, CredentialPurpose.EmailAccount, EmailAccountProfile.Custom("recovery@example.test")), 0);
            await watches.ConfigureAsync(id, 0, true, 60, now);
            var baseline = Assert.Single(await watches.ClaimDueAsync(now, 1));
            await watches.RecordFailureAsync(baseline, new("email_watch_access_required", null, true), now);
            await watches.CheckNowAsync(id, 1, now.AddSeconds(1));
            if (sqlite) watches = new SqliteEmailWatchStore(factory);
            var queued = await watches.GetAsync(id); Assert.True(queued.Paused); Assert.True(queued.RecoveryCheckRequested);
            var interrupted = Assert.Single(await watches.ClaimDueAsync(now.AddSeconds(1), 1)); Assert.True(interrupted.State.Paused);
            if (sqlite) watches = new SqliteEmailWatchStore(factory);
            var recovered = Assert.Single(await watches.ClaimDueAsync(now.AddSeconds(92), 1)); Assert.True(recovered.State.Paused);
            Assert.False(await watches.CommitAsync(interrupted, new([], new(EmailWatchCursorKind.Synthetic)), [], now.AddSeconds(93)));
            Assert.True(await watches.CommitAsync(recovered, new([], new(EmailWatchCursorKind.Synthetic)), [], now.AddSeconds(93)));
            var success = await watches.GetAsync(id); Assert.False(success.Paused); Assert.False(success.RecoveryCheckRequested);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private sealed class Factory(string path) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options); }
}
