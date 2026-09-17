using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class AttachmentStoreContractTests
{
    [Fact]
    public async Task Bytes_stay_out_of_sqlite_and_ttl_does_not_delete_bound_blobs()
    {
        await using var harness = await SqliteAsync();
        var time = harness.Time;
        var text = "hello notes"u8.ToArray();
        var pending = await harness.Store.UploadPendingAsync(
            harness.SessionId,
            "../secret/notes.txt",
            "text/plain",
            new MemoryStream(text),
            allowStoreUnread: false);
        Assert.Equal("notes.txt", pending.DisplayName);
        Assert.Equal(AttachmentState.Pending, pending.State);
        Assert.DoesNotContain(Convert.ToHexString(text), pending.BlobKey, StringComparison.OrdinalIgnoreCase);

        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.Attachments.AsNoTracking().SingleAsync();
            Assert.DoesNotContain("hello", row.DisplayName + row.BlobKey + row.Sha256Hex, StringComparison.Ordinal);
            Assert.Null(row.EntryId);
        }

        var bound = await harness.Store.BindToEntryAsync(harness.SessionId, Guid.NewGuid(), [pending.AttachmentId]);
        Assert.Equal(pending.Sha256Hex, bound[0].Sha256Hex);
        time.Advance(AttachmentLimits.PendingTtl + TimeSpan.FromMinutes(1));
        await harness.Store.SweepExpiredAsync();
        var still = await harness.Store.GetAsync(harness.SessionId, pending.AttachmentId);
        Assert.Equal(AttachmentState.Bound, still!.State);
        await using var content = await harness.Store.OpenContentAsync(harness.SessionId, pending.AttachmentId);
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal(text, buffer.ToArray());
    }

    [Fact]
    public async Task Cancelled_upload_does_not_commit_and_expired_pending_cannot_bind()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryAttachmentStore(time);
        var session = Guid.CreateVersion7();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.UploadPendingAsync(session, "a.txt", "text/plain", new MemoryStream("abc"u8.ToArray()), false, cts.Token)
                .AsTask());
        Assert.Null(await store.GetAsync(session, Guid.CreateVersion7()));

        var pending = await store.UploadPendingAsync(
            session,
            "a.txt",
            "text/plain",
            new MemoryStream("abc"u8.ToArray()),
            false);
        time.Advance(AttachmentLimits.PendingTtl + TimeSpan.FromSeconds(1));
        await store.SweepExpiredAsync();
        Assert.Null(await store.GetAsync(session, pending.AttachmentId));
        var expired = await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.BindToEntryAsync(session, Guid.NewGuid(), [pending.AttachmentId]).AsTask());
        Assert.Equal("NotFound", expired.Code);
    }

    [Fact]
    public async Task Unread_policy_stores_unknown_types_and_rejects_without_it()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.CreateVersion7();
            var opaque = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var denied = await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.UploadPendingAsync(session, "blob.bin", "application/octet-stream", new MemoryStream(opaque), false)
                .AsTask());
        Assert.Equal("ValidationError", denied.Code);
        var unread = await store.UploadPendingAsync(
            session,
            "blob.bin",
            "application/octet-stream",
            new MemoryStream(opaque),
            allowStoreUnread: true);
        Assert.False(unread.Readable);
        var zip = await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.UploadPendingAsync(session, "x.zip", "application/zip", new MemoryStream("PK\u0003\u0004abcd"u8.ToArray()), true)
                .AsTask());
        Assert.Equal("ValidationError", zip.Code);
    }

    [Fact]
    public async Task Session_delete_removes_blobs()
    {
        await using var harness = await SqliteAsync();
        var pending = await harness.Store.UploadPendingAsync(
            harness.SessionId,
            "a.txt",
            "text/plain",
            new MemoryStream("abc"u8.ToArray()),
            false);
        var path = Path.Combine(harness.Root, pending.BlobKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path));
        await harness.Store.DeleteSessionAsync(harness.SessionId);
        Assert.False(File.Exists(path));
        Assert.Null(await harness.Store.GetAsync(harness.SessionId, pending.AttachmentId));
    }

    private static async Task<SqliteAttachmentHarness> SqliteAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-att-{Guid.NewGuid():N}.db");
        var root = Path.Combine(Path.GetTempPath(), $"agent-core-blobs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var memory = new SqliteMemoryStore(factory, time);
        await memory.EnsureCreatedAsync();
        return new SqliteAttachmentHarness(path, root, factory, new SqliteAttachmentStore(factory, time, root), time, Guid.CreateVersion7());
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SqliteAttachmentHarness(
        string dbPath,
        string root,
        IDbContextFactory<AgentCoreDbContext> factory,
        SqliteAttachmentStore store,
        FakeTimeProvider time,
        Guid sessionId) : IAsyncDisposable
    {
        public IDbContextFactory<AgentCoreDbContext> Factory => factory;
        public SqliteAttachmentStore Store => store;
        public FakeTimeProvider Time => time;
        public Guid SessionId => sessionId;
        public string Root => root;

        public async ValueTask DisposeAsync()
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                SqliteConnection.ClearPool(connection);
            }
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            await Task.CompletedTask;
        }
    }
}
