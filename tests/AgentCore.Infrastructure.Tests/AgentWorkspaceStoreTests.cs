using System.Security.Cryptography;
using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Definitions;
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
            var item = await store.WriteFileAsync(a, "/home/reports/exact.bin", "application/octet-stream", bytes, sourceSession, null, null);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), item.Sha256Hex);
            Assert.Equal(bytes, (await store.ReadAsync(a, item.ItemId, null)).Bytes);
            Assert.Empty((await store.ListAsync(b, "/home", null, 10)).Items);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.ReadAsync(b, item.ItemId, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, item.LogicalPath, item.ContentType, bytes, sourceSession, null, null));
            var newer = await store.WriteFileAsync(a, item.LogicalPath, item.ContentType, "revision two"u8.ToArray(), sourceSession, item.Revision, item.Sha256Hex);
            Assert.Equal(item.ItemId, newer.ItemId); Assert.Equal(2, newer.Revision);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, item.LogicalPath, item.ContentType, bytes, sourceSession, 1, null));
            Assert.Equal("revision two"u8.ToArray(), (await store.ReadAsync(a, item.ItemId, null)).Bytes);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.DeleteAsync(a, item.ItemId, 1));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, "/home/too-large", "text/plain", new byte[33], sourceSession, null, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, "/home/aggregate-limit", "text/plain", new byte[30], sourceSession, null, null));
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
            var item = await store.WriteFileAsync(a, "/home/Reports/a.md", "text/markdown", "first"u8.ToArray(), null, null, null);
            var results = await Task.WhenAll(Replace("A"), Replace("B"));
            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(2, (await store.ReadAsync(a, item.ItemId, null)).Item.Revision);
            foreach (var path in new[] { "/home/../a", "/etc/passwd", "/home/.env", "/home/CON.txt", "/home/a/", "/home/a\\b", "/home/Reports/A.md", "/home/reports/b.md", "/home/Reports/a.md/child", "/home/Reports" })
                await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, path, "text/plain", "x"u8.ToArray(), null, null, null));
            var page = await store.ListAsync(a, "/home", null, 1);
            Assert.Single(page.Items);
            async Task<bool> Replace(string body)
            {
                try { await store.WriteFileAsync(a, item.LogicalPath, "text/plain", System.Text.Encoding.UTF8.GetBytes(body), null, 1, null); return true; }
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
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(a, "/home/a", "text/plain", "x"u8.ToArray(), null, null, null));
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
            var original = await store.WriteFileAsync(owner, "/home/report.md", "text/markdown", "original"u8.ToArray(), null, null, null);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_home_insert BEFORE INSERT ON AgentWorkspaceItems BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_home_update BEFORE UPDATE ON AgentWorkspaceItems BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
            }
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(owner, "/home/new.md", "text/plain", "new"u8.ToArray(), null, null, null));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.WriteFileAsync(owner, original.LogicalPath, "text/plain", "replacement"u8.ToArray(), null, original.Revision, original.Sha256Hex));
            store = NewStore(root, factory);
            var retained = await store.ReadAsync(owner, original.ItemId, null);
            Assert.Equal(original, retained.Item); Assert.Equal("original"u8.ToArray(), retained.Bytes);
            Assert.Single((await store.ListAsync(owner, "/home", null, 10)).Items);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "blobs", owner.ToString("N"))));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_store_tree_copy_preserves_binary_empty_directories_and_preflights_quotas(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "workspace-transfer", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = sqlite ? new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/store.db").Options) : null;
            if (factory is not null) await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var home = NewStore(root, factory); var owner = Guid.NewGuid(); var session = Guid.NewGuid();
            var scratch = new FileSessionWorkspace(Path.Combine(root, "scratch"), Path.Combine(root, "templates"), maxWritableBytes: 8);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "agents"))) repository = repository.Parent;
            var definition = (await new FileAgentDefinitionStore(Path.Combine(repository!.FullName, "agents"), SyntheticProviderAliases.Default).GetAsync("general-assistant", 16))!;
            await scratch.EnsureAsync(session, definition);
            byte[] bytes = [0, 255, 128, 10, 13];
            var tree = new WorkspaceTransfer([new("", true, "inode/directory", []), new("empty", true, "inode/directory", []), new("data.bin", false, "application/octet-stream", bytes)]);
            await home.ImportAsync(owner, "/home/project", tree);
            await scratch.ImportAsync(session, "/workspace/working/copied", await home.ExportAsync(owner, "/home/project"));
            Assert.Equal(bytes, (await scratch.ReadAsync(session, definition, "/workspace/working/copied/data.bin")).Bytes);
            Assert.Contains(await scratch.ListAsync(session, definition, "/workspace/working/copied"), n => n.Directory && n.LogicalPath.EndsWith("/empty", StringComparison.Ordinal));
            var transfer = await scratch.ExportAsync(session, "/workspace/working/copied");
            await home.ImportAsync(owner, "/home/roundtrip", transfer, session);
            Assert.Equal(bytes, (await home.ReadAsync(owner, null, "/home/roundtrip/data.bin")).Bytes);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await scratch.ImportAsync(session, "/workspace/working/over-quota", transfer));
            Assert.False(Directory.Exists(Path.Combine(scratch.PhysicalWorkingDirectory(session), "over-quota")));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await home.ImportAsync(owner, "/home/project", transfer));
            var large = new WorkspaceTransfer([new("", true, "inode/directory", []), new("big", false, "text/plain", new byte[31])]);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await home.ImportAsync(owner, "/home/over-quota", large));
            Assert.DoesNotContain((await home.ListAsync(owner, "/home", null, 256)).Items, n => n.LogicalPath.StartsWith("/home/over-quota", StringComparison.Ordinal));
            var existing = (await home.ReadAsync(owner, null, "/home/project/data.bin")).Item;
            var replacement = new WorkspaceTransfer([new("", false, "application/octet-stream", new byte[] { 42, 0 })]);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await home.ImportAsync(owner, existing.LogicalPath, replacement));
            await home.ImportAsync(owner, existing.LogicalPath, replacement, session, existing.Revision, existing.Sha256Hex);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await home.ImportAsync(owner, existing.LogicalPath, replacement, session, existing.Revision, existing.Sha256Hex));
            if (sqlite) home = NewStore(root, factory);
            var overwritten = await home.ReadAsync(owner, null, existing.LogicalPath);
            Assert.Equal(new byte[] { 42, 0 }, overwritten.Bytes);
            Assert.Equal(session, overwritten.Item.SourceSessionId);
            Assert.All((await home.ListAsync(owner, "/home/roundtrip", null, 256)).Items,
                item => Assert.Equal(session, item.SourceSessionId));
            Assert.Contains((await home.ListAsync(owner, "/home", null, 256)).Items, n => n.LogicalPath == "/home/roundtrip/empty" && n.Directory);
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
