using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class ContinuityCadenceStoreTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cadence_revisions_and_claims_are_independent_atomic_and_restart_safe(bool sqlite)
    {
        await IdentityConsolidationContractTests.WithStores(sqlite, async (_, experience, factory) =>
        {
            var store = (IContinuityMaintenanceStore)experience;
            var id = Guid.NewGuid();
            var initial = await store.ReadAsync(id);
            Assert.Equal(new(id, null, 0, null), initial);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(1791240000000);
            var claims = await Task.WhenAll(store.TryClaimAsync(initial, now).AsTask(), store.TryClaimAsync(initial, now).AsTask());
            Assert.Single(claims, value => value);
            var claimed = await store.ReadAsync(id);
            Assert.Equal(0, claimed.Revision);
            Assert.Equal(now, claimed.LastMaintenanceAtUtc);
            var configured = await store.ConfigureAsync(id, 0, 900);
            Assert.Equal(1, configured.Revision); Assert.Equal(now, configured.LastMaintenanceAtUtc);
            Assert.False(await store.TryClaimAsync(claimed, now.AddMinutes(5)));
            Assert.Equal(409, (await Assert.ThrowsAsync<AgentCoreException>(() => store.ConfigureAsync(id, 0, 300).AsTask())).StatusCode);
            Assert.Equal(400, (await Assert.ThrowsAsync<AgentCoreException>(() => store.ConfigureAsync(id, 1, 0).AsTask())).StatusCode);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureAsync(id, 1, 60, cancelled.Token).AsTask());
            IContinuityMaintenanceStore reopened = factory is null ? store : new SqliteExperienceStore(factory);
            Assert.Equal(configured, await reopened.ReadAsync(id));
            Assert.True(await reopened.TryClaimAsync(configured, now.AddMinutes(15)));
            var inherited = await reopened.ConfigureAsync(id, 1, null);
            Assert.Null(inherited.IntervalSeconds); Assert.Equal(2, inherited.Revision);
            Assert.Equal(now.AddMinutes(15), inherited.LastMaintenanceAtUtc);
            Assert.False((await experience.SettingsAsync(id)).Enabled);
            Assert.False((await experience.MaintenanceSettingsAsync(id)).AllowAgentConsolidation);
        });
    }

    [Fact]
    public async Task Pre_cadence_database_upgrades_with_inherited_settings_and_retained_experience()
    {
        await IdentityConsolidationContractTests.WithStores(true, async (_, experience, factory) =>
        {
            var id = Guid.NewGuid();
            await experience.ConfigureAsync(id, 0, true);
            await using (var db = factory!.CreateDbContext())
                await db.GetService<IMigrator>().MigrateAsync("20261006042725_P910MaintenanceProvenance");
            await new SqliteMemoryStore(factory!, TimeProvider.System).EnsureCreatedAsync();
            Assert.True((await experience.SettingsAsync(id)).Enabled);
            Assert.Equal(new(id, null, 0, null), await ((IContinuityMaintenanceStore)experience).ReadAsync(id));
        });
    }
}
