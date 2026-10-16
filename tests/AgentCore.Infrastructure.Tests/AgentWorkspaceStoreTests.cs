using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentWorkspaceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Path_reads_observe_shared_bytes_and_reopen_without_file_rows_or_prior_read_tokens(bool sqlite)
    {
        using var fixture = await Fixture.Create(sqlite);
        var store = fixture.Store(); byte[] bytes = [0, 255, 128, 13, 10];
        var item = await store.WriteFileAsync(fixture.Owner, "/home/project/raw.bin", bytes);
        Assert.Equal(bytes, (await store.ReadAsync(fixture.Owner, item.LogicalPath)).Bytes);
        var physical = Path.Combine(AgentWorkspacePhysicalPaths.HomeDirectory(fixture.Workspaces, fixture.Owner), "project", "raw.bin");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(physical));
        await File.WriteAllTextAsync(physical, "external process");
        Assert.Equal("external process", Encoding.UTF8.GetString((await fixture.Store().ReadAsync(fixture.Owner, item.LogicalPath)).Bytes));
        await store.WriteFileAsync(fixture.Owner, item.LogicalPath, "new writer"u8.ToArray());
        Assert.Equal("new writer", await File.ReadAllTextAsync(physical));
        Assert.Empty((await store.ListAsync(fixture.Other, "/home", null, 10)).Items);
        await Assert.ThrowsAsync<AgentCoreException>(() => store.ReadAsync(fixture.Other, item.LogicalPath).AsTask());
        Assert.Empty((await store.InspectAsync(fixture.Owner, item.LogicalPath, 0)).Bytes);
        Assert.Equal("new writer"u8.ToArray(), (await store.InspectAsync(fixture.Owner, item.LogicalPath, 32)).Bytes);
        if (fixture.Contexts is not null)
        {
            await using var db = fixture.Contexts.CreateDbContext();
            await db.Database.OpenConnectionAsync(); using var query = db.Database.GetDbConnection().CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AgentWorkspaceItems'";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_repository_names_deep_paths_empty_directories_and_concurrent_writers_survive(bool sqlite)
    {
        using var fixture = await Fixture.Create(sqlite); var store = fixture.Store();
        var deep = "/home/" + string.Join('/', Enumerable.Repeat("deep", 12));
        var made = await store.StructureAsync(fixture.Owner, [new("mkdir", Path: deep + "/empty")]); Assert.True(made.Completed);
        foreach (var name in new[] { ".git/config", ".env", "appsettings.json", "runtime/data", Guid.NewGuid().ToString("D") })
            await store.WriteFileAsync(fixture.Owner, "/home/" + name, "normal"u8.ToArray());
        var path = "/home/concurrent";
        await Task.WhenAll(store.WriteFileAsync(fixture.Owner, path, "A"u8.ToArray()).AsTask(), store.WriteFileAsync(fixture.Owner, path, "B"u8.ToArray()).AsTask());
        Assert.Contains(Encoding.UTF8.GetString((await fixture.Store().ReadAsync(fixture.Owner, path)).Bytes), new[] { "A", "B" });
        var page = await fixture.Store().ListAsync(fixture.Owner, deep, null, 10);
        Assert.Contains(page.Items, i => i.LogicalPath == deep + "/empty" && i.Directory);
        Assert.Equal("sampled", page.UsageEnforcement);
    }

    [Fact]
    public async Task Owner_validation_and_current_policy_protect_roots_and_bounded_mutations()
    {
        using var fixture = await Fixture.Create(false); var store = fixture.Store(8, 12);
        await store.WriteFileAsync(fixture.Owner, "/home/file", "12345678"u8.ToArray());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteFileAsync(fixture.Owner, "/home/too-big", new byte[9]).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteFileAsync(fixture.Owner, "/home/aggregate", new byte[5]).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteFileAsync(Guid.NewGuid(), "/home/file", "x"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteFileAsync(fixture.Owner, "/home/../foreign", "x"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.DeleteInstanceContentAsync(fixture.Owner).AsTask());
        await fixture.Instances.UpdateWithExpectedRevisionAsync(new(fixture.Owner, 1, Lifecycle: AgentInstanceLifecycle.Archived), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<AgentCoreException>(() => store.WriteFileAsync(fixture.Owner, "/home/file", "x"u8.ToArray()).AsTask());
        Assert.Equal("12345678"u8.ToArray(), (await store.ReadAsync(fixture.Owner, "/home/file")).Bytes);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "canonical-home-" + Guid.NewGuid().ToString("N"));
        internal string Workspaces => Path.Combine(Root, "workspaces");
        internal Guid Owner { get; } = Guid.NewGuid(); internal Guid Other { get; } = Guid.NewGuid();
        internal IAgentInstanceStore Instances = null!; internal Factory? Contexts;
        internal InMemorySandboxStore Sandboxes { get; } = new();
        internal FileAgentInstanceWorkspaceStore Store(long maxFile = 50 * 1024 * 1024, long maxRoot = 250 * 1024 * 1024) => new(Workspaces, Instances, Sandboxes, maxFile, maxRoot);
        internal static async Task<Fixture> Create(bool sqlite)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            if (sqlite)
            {
                f.Contexts = new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + Path.Combine(f.Root,"core.db")).Options);
                await new SqliteMemoryStore(f.Contexts, TimeProvider.System).EnsureCreatedAsync();
                f.Instances = new SqliteAgentInstanceStore(f.Contexts, new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System));
            }
            else f.Instances = new InMemoryAgentInstanceStore();
            foreach (var id in new[] { f.Owner, f.Other }) await f.Instances.InsertAsync(new(id,"examiner",1,new("Alex","Role","Description","Tone"),AgentInstanceLifecycle.Active,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));
            return f;
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
