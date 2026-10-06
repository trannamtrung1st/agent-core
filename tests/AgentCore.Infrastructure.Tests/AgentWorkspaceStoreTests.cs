using System.Security.Cryptography;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentWorkspaceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_bytes_isolation_CAS_quota_and_restart(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-home-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = sqlite ? new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db").Options) : null;
            if (factory is not null) await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = NewStore(root, factory);
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var sourceSession = Guid.NewGuid();
            byte[] bytes = [0, 255, 10, 13, 128];
            var item = await store.RetainAsync(a, "/home/reports/exact.bin", "application/octet-stream", bytes, sourceSession, null, null);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), item.Sha256Hex);
            Assert.Equal(bytes, (await store.ReadAsync(a, item.ItemId, null)).Bytes);
            Assert.Empty((await store.ListAsync(b, "/home", null, 10)).Items);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.ReadAsync(b, item.ItemId, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, item.LogicalPath, item.ContentType, bytes, sourceSession, null, null));
            var newer = await store.RetainAsync(a, item.LogicalPath, item.ContentType, "revision two"u8.ToArray(), sourceSession, item.Revision, item.Sha256Hex);
            Assert.Equal(item.ItemId, newer.ItemId); Assert.Equal(2, newer.Revision);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, item.LogicalPath, item.ContentType, bytes, sourceSession, 1, null));
            Assert.Equal("revision two"u8.ToArray(), (await store.ReadAsync(a, item.ItemId, null)).Bytes);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.DeleteAsync(a, item.ItemId, 1));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, "/home/too-large", "text/plain", new byte[33], sourceSession, null, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, "/home/aggregate-limit", "text/plain", new byte[30], sourceSession, null, null));
            Assert.Single((await store.ListAsync(a, "/home", null, 10)).Items, i => !i.Directory);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "blobs", a.ToString("N"))));
            if (factory is not null)
            {
                var orphan = Path.Combine(root, "blobs", a.ToString("N"), Guid.NewGuid().ToString("N") + ".partial");
                await File.WriteAllTextAsync(orphan, "uncommitted bytes");
                store = NewStore(root, factory);
                Assert.Equal("revision two"u8.ToArray(), (await store.ReadAsync(a, null, item.LogicalPath)).Bytes);
                Assert.False(File.Exists(orphan));
            }
            await store.DeleteInstanceAsync(a);
            await store.DeleteInstanceAsync(a);
            Assert.Empty((await store.ListAsync(a, "/home", null, 10)).Items);
            Assert.False(Directory.Exists(Path.Combine(root, "blobs", a.ToString("N"))));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_replacements_have_one_winner_and_paths_are_portable(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-home-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = sqlite ? new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db").Options) : null;
            if (factory is not null) await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = NewStore(root, factory); var a = Guid.NewGuid();
            var item = await store.RetainAsync(a, "/home/Reports/a.md", "text/markdown", "first"u8.ToArray(), null, null, null);
            var results = await Task.WhenAll(Replace("A"), Replace("B"));
            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(2, (await store.ReadAsync(a, item.ItemId, null)).Item.Revision);
            foreach (var path in new[] { "/home/../a", "/etc/passwd", "/home/.env", "/home/CON.txt", "/home/a/", "/home/a\\b", "/home/Reports/A.md", "/home/reports/b.md", "/home/Reports/a.md/child", "/home/Reports" })
                await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, path, "text/plain", "x"u8.ToArray(), null, null, null));
            var page = await store.ListAsync(a, "/home", null, 1);
            Assert.Single(page.Items);
            async Task<bool> Replace(string body)
            {
                try { await store.RetainAsync(a, item.LogicalPath, "text/plain", System.Text.Encoding.UTF8.GetBytes(body), null, 1, null); return true; }
                catch (AgentCoreException e) when (e.Code == "Conflict") { return false; }
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Linked_owner_directory_cannot_write_or_clean_another_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-home-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside"); var blobs = Path.Combine(root, "blobs");
        Directory.CreateDirectory(outside); Directory.CreateDirectory(blobs);
        var a = Guid.NewGuid(); Directory.CreateSymbolicLink(Path.Combine(blobs, a.ToString("N")), outside);
        try
        {
            var store = NewStore(root, null);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(a, "/home/a", "text/plain", "x"u8.ToArray(), null, null, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.DeleteInstanceAsync(a));
            Assert.True(Directory.Exists(outside)); Assert.Empty(Directory.GetFiles(outside));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_metadata_commit_removes_new_blob_and_preserves_previous_revision()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-home-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db").Options);
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = NewStore(root, factory); var owner = Guid.NewGuid();
            var original = await store.RetainAsync(owner, "/home/report.md", "text/markdown", "original"u8.ToArray(), null, null, null);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_home_insert BEFORE INSERT ON AgentWorkspaceItems BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_home_update BEFORE UPDATE ON AgentWorkspaceItems BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
            }
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(owner, "/home/new.md", "text/plain", "new"u8.ToArray(), null, null, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.RetainAsync(owner, original.LogicalPath, "text/plain", "replacement"u8.ToArray(), null, original.Revision, original.Sha256Hex));
            store = NewStore(root, factory);
            var retained = await store.ReadAsync(owner, original.ItemId, null);
            Assert.Equal(original, retained.Item); Assert.Equal("original"u8.ToArray(), retained.Bytes);
            Assert.Single((await store.ListAsync(owner, "/home", null, 10)).Items);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "blobs", owner.ToString("N"))));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static FileAgentInstanceWorkspaceStore NewStore(string root, Factory? factory) =>
        new(Path.Combine(root, "blobs"), TimeProvider.System, new SystemIdGenerator(TimeProvider.System), factory, maxFileBytes: 32, maxInstanceBytes: 40);
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
