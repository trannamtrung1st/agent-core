using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkCaptureRetentionTests
{
    [Fact]
    public async Task Sqlite_purge_deletes_expired_capture_files_without_another_save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-captures-{Guid.NewGuid():N}.db");
        var root = Path.Combine(Path.GetTempPath(), $"agent-core-captures-{Guid.NewGuid():N}");
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-03T00:00:00Z"));
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SqlitePragmaInterceptor(5_000))
            .Options;
        var contexts = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(contexts, clock).EnsureCreatedAsync();
            var store = new SqliteWorkCaptureStore(contexts, clock, root);
            var saved = await store.SaveAsync(
                Guid.Parse("019944af-00e9-7000-8000-000000000001"),
                Guid.Parse("019944af-00e9-7000-8000-000000000002"),
                "image/png",
                new byte[] { 1, 2, 3, 4 });
            var loaded = await store.ReadAsync(saved.Capture!.CaptureId);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded!.Bytes);
            var file = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Single();
            clock.Advance(TimeSpan.FromDays(8));
            Assert.Equal(1, await store.PurgeExpiredAsync());
            Assert.False(File.Exists(file));
            Assert.Null(await store.ReadAsync(saved.Capture.CaptureId));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCoreDbContext(options));
    }
}
