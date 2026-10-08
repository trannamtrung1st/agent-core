using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class ArtifactStoreContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifact_pages_are_bounded_and_foreign_session_cursors_are_rejected(bool sqlite)
    {
        await using var harness = sqlite ? await SqliteAsync() : null;
        IArtifactStore store = harness is null ? new InMemoryArtifactStore(TimeProvider.System) : harness.Store;
        var sessionId = harness?.SessionId ?? Guid.NewGuid();
        var files = new List<ArtifactRecord>();
        for (var index = 0; index < 5; index++)
            files.Add(await store.CreateAsync(sessionId, $"report-{index}.md", "text/markdown", new byte[] { 1 }, null, null));
        var expected = files.OrderByDescending(file => file.CreatedAt).ThenByDescending(file => file.ArtifactId.ToString("D"), StringComparer.Ordinal).Select(file => file.ArtifactId).ToArray();
        var seen = new List<Guid>(); Guid? cursor = null;
        do
        {
            var page = await store.ListPageAsync(sessionId, cursor, 2);
            Assert.InRange(page.Items.Count, 1, 2);
            Assert.All(page.Items, file => Assert.Equal(sessionId, file.SessionId));
            seen.AddRange(page.Items.Select(file => file.ArtifactId)); cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.HasMore);
        } while (cursor is not null);
        Assert.Equal(expected, seen);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.ListPageAsync(Guid.NewGuid(), files[0].ArtifactId, 2).AsTask())).Code);
    }

    [Fact]
    public async Task Sqlite_blobs_stay_out_of_rows_and_delete_does_not_resurrect()
    {
        await using var harness = await SqliteAsync();
        var bytes = "artifact-bytes"u8.ToArray();
        var created = await harness.Store.CreateAsync(
            harness.SessionId,
            "report.md",
            "text/markdown",
            bytes,
            sourceAttachmentId: null,
            workspaceLogicalPath: "/workspace/working/report.md");
        Assert.Equal(bytes.Length, created.ByteSize);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.Artifacts.AsNoTracking().SingleAsync();
            Assert.DoesNotContain("artifact-bytes", row.DisplayName + row.BlobKey + row.Sha256Hex, StringComparison.Ordinal);
        }

        await using var content = await harness.Store.OpenContentAsync(harness.SessionId, created.ArtifactId);
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal(bytes, buffer.ToArray());

        var oversized = await Assert.ThrowsAsync<AgentCoreException>(() =>
            harness.Store.CreateAsync(harness.SessionId, "big.bin", "application/octet-stream", new byte[40], null, null).AsTask());
        Assert.Equal("ArtifactQuotaExceeded", oversized.Code);

        await harness.Store.DeleteSessionAsync(harness.SessionId);
        Assert.Null(await harness.Store.GetAsync(harness.SessionId, created.ArtifactId));
        var late = await Assert.ThrowsAsync<AgentCoreException>(() =>
            harness.Store.CreateAsync(harness.SessionId, "late.bin", "application/octet-stream", "x"u8.ToArray(), null, null).AsTask());
        Assert.Equal("NotFound", late.Code);
    }

    [Fact]
    public async Task Sqlite_deletes_final_blob_when_database_save_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-art-{Guid.NewGuid():N}.db");
        var root = Path.Combine(Path.GetTempPath(), $"agent-core-art-blobs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new FailArtifactSaveInterceptor())
            .Options;
        var factory = new TestFactory(options);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var memory = new SqliteMemoryStore(factory, time);
        await memory.EnsureCreatedAsync();
        var store = new SqliteArtifactStore(factory, time, root, maxBytesEach: 32, maxBytesSession: 32);
        var sessionId = Guid.CreateVersion7();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(sessionId, "report.md", "text/markdown", "artifact-bytes"u8.ToArray(), null, null).AsTask());
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
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

    private sealed class FailArtifactSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfArtifactInsert(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArtifactInsert(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void ThrowIfArtifactInsert(DbContextEventData eventData)
        {
            if (eventData.Context?.ChangeTracker.Entries<ArtifactRecordRow>().Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException("simulated artifact save failure");
            }
        }
    }

    private static async Task<SqliteArtifactHarness> SqliteAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-art-{Guid.NewGuid():N}.db");
        var root = Path.Combine(Path.GetTempPath(), $"agent-core-art-blobs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var memory = new SqliteMemoryStore(factory, time);
        await memory.EnsureCreatedAsync();
        return new SqliteArtifactHarness(
            path,
            root,
            factory,
            new SqliteArtifactStore(factory, time, root, maxBytesEach: 32, maxBytesSession: 32),
            Guid.CreateVersion7());
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SqliteArtifactHarness(
        string dbPath,
        string root,
        IDbContextFactory<AgentCoreDbContext> factory,
        SqliteArtifactStore store,
        Guid sessionId) : IAsyncDisposable
    {
        public IDbContextFactory<AgentCoreDbContext> Factory { get; } = factory;
        public SqliteArtifactStore Store { get; } = store;
        public Guid SessionId { get; } = sessionId;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (IOException)
            {
            }

            await ValueTask.CompletedTask;
        }
    }
}
