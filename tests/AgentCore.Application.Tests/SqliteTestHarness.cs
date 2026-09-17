using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

internal sealed class SqliteTestHarness : IAsyncDisposable
{
    private readonly string _path;
    private readonly bool _deleteOnDispose;

    private SqliteTestHarness(
        string path,
        IDbContextFactory<AgentCoreDbContext> factory,
        SqliteMemoryStore store,
        bool deleteOnDispose)
    {
        _path = path;
        _deleteOnDispose = deleteOnDispose;
        Factory = factory;
        Store = store;
    }

    public IDbContextFactory<AgentCoreDbContext> Factory { get; }

    public SqliteMemoryStore Store { get; }

    public static async Task<SqliteTestHarness> CreateMigratedAsync()
    {
        var harness = Open(deleteOnDispose: true);
        await harness.Store.EnsureCreatedAsync();
        return harness;
    }

    public static SqliteTestHarness Open(bool deleteOnDispose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        return Open(path, deleteOnDispose);
    }

    public static SqliteTestHarness Open(string path, bool deleteOnDispose)
    {
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new ContextFactory(options);
        var store = new SqliteMemoryStore(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)));
        return new SqliteTestHarness(path, factory, store, deleteOnDispose);
    }

    public ValueTask DisposeAsync()
    {
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            SqliteConnection.ClearPool(connection);
        }
        if (_deleteOnDispose)
        {
            foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
