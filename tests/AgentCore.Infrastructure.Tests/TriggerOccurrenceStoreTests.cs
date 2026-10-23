using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class TriggerOccurrenceStoreTests
{
    private static readonly Guid InstanceA = Guid.Parse("019944af-0009-7000-8000-0000000000a1");
    private static readonly Guid ProfileA = Guid.Parse("019944af-0009-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_claims_return_to_pending_and_a_second_claim_loses()
    {
        await ForEachStore(async store =>
        {
            var now = Due;
            var occurrence = new TriggerOccurrence(
                Guid.NewGuid(),
                $"schedule:{Guid.NewGuid():D}",
                null,
                new TriggerOwner(InstanceA, ProfileA),
                TriggerSourceKind.Schedule,
                now,
                now,
                now,
                "{}",
                null,
                1,
                OccurrenceRoutingDisposition.Pending,
                null,
                0,
                null,
                null,
                null);
            await store.AdmitOccurrenceAsync(occurrence);
            var claim = Guid.NewGuid();
            Assert.NotNull(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, now.AddSeconds(30), now));
            Assert.Null(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, Guid.NewGuid(), now.AddSeconds(30), now));
            Assert.Equal(1, await store.RecoverExpiredClaimsAsync(now.AddSeconds(30)));
            var pending = await store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
            Assert.Equal(occurrence.OccurrenceId, Assert.Single(pending).OccurrenceId);
        });
    }

    private static FakeTimeProvider Clock() => new(Created);

    private static async Task ForEachStore(Func<ITriggerStore, Task> exercise)
    {
        await exercise(new InMemoryTriggerStore());
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-schedule-{Guid.NewGuid():N}.db");
        var factory = new TestFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, Clock()).EnsureCreatedAsync();
            await exercise(new SqliteTriggerStore(factory));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
