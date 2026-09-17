using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class MemoryStoreContractTests
{
    [Fact]
    public async Task In_memory_and_sqlite_share_revision_idempotency_and_conflict()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        foreach (var store in stores)
        {
            var snapshot = First();
            await store.SaveAsync(snapshot, 0);
            await store.SaveAsync(snapshot, 0);
            var loaded = await store.LoadAsync(snapshot.SessionId);
            Assert.Equal(1, loaded!.Revision);
            var stale = snapshot with { Revision = 1, Summary = "stale" };
            var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => store.SaveAsync(stale, 0).AsTask());
            Assert.Equal("Conflict", conflict.Code);
            var next = snapshot with { Revision = 2, Summary = "ok" };
            await store.SaveAsync(next, 1);
            loaded = await store.LoadAsync(snapshot.SessionId);
            Assert.Equal("ok", loaded!.Summary);
            Assert.Equal(2, loaded.Revision);
        }
    }

    [Fact]
    public async Task Catalog_pagination_excludes_deleted_and_archived_by_default()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        foreach (var store in stores)
        {
            var newer = First() with
            {
                SessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b801"),
                UpdatedAt = new DateTimeOffset(2026, 9, 16, 0, 0, 2, TimeSpan.Zero),
                Title = "Newer"
            };
            var archived = First() with
            {
                SessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b802"),
                UpdatedAt = new DateTimeOffset(2026, 9, 16, 0, 0, 1, TimeSpan.Zero),
                Title = "Archived",
                ArchivedAt = new DateTimeOffset(2026, 9, 16, 0, 0, 3, TimeSpan.Zero)
            };
            var deleted = First() with
            {
                SessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b803"),
                Title = "Gone",
                DurablyDeletedAt = new DateTimeOffset(2026, 9, 16, 0, 0, 4, TimeSpan.Zero)
            };
            await store.SaveAsync(newer, 0);
            await store.SaveAsync(archived, 0);
            await store.SaveAsync(deleted, 0);
            var page = await store.ListCatalogAsync(null, 50, includeArchived: false);
            Assert.Single(page.Items);
            Assert.Equal(newer.SessionId, page.Items[0].SessionId);
            var withArchived = await store.ListCatalogAsync(null, 50, includeArchived: true);
            Assert.Equal(2, withArchived.Items.Count);
        }
    }

    [Fact]
    public async Task Sqlite_transaction_rollback_does_not_keep_session()
    {
        await using var harness = await SqliteAsync();
        var id = Guid.NewGuid().ToString("D");
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Sessions.Add(new SessionRecord
            {
                SessionId = id,
                AgentId = "examiner",
                AgentVersion = 1,
                DefinitionJson = "{}",
                Mode = nameof(SessionMode.Text),
                Status = nameof(SessionStatus.Created),
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                Revision = 1,
                Snapshot = new SnapshotRecord { SessionId = id, SchemaVersion = 1 }
            });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var verify = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Sessions.CountAsync());
    }

    [Fact]
    public async Task Reopen_after_crash_pauses_attached_and_interrupts_streaming()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var id = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var streaming = Entry(id, 1, EntryStatus.Streaming, "Hello extra");
        var first = First() with
        {
            Status = SessionStatus.Attached,
            PendingMode = SessionMode.Voice,
            Entries = [streaming]
        };
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await opened.Store.EnsureCreatedAsync();
            await opened.Store.SaveAsync(first, 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        await reopened.Store.RecoverCrashedSessionsAsync();
        var loaded = await reopened.Store.LoadAsync(first.SessionId);
        Assert.Equal(SessionStatus.Paused, loaded!.Status);
        Assert.Null(loaded.PendingMode);
        Assert.Equal(EntryStatus.Interrupted, loaded.Entries[0].Status);
        Assert.Equal(streaming.HeardTextEndExclusive, loaded.Entries[0].HeardTextEndExclusive);
    }

    [Fact]
    public async Task Recover_keeps_trailing_queued_users_after_interrupting_streaming()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var id = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var u1Id = Guid.Parse("019944af-0000-7000-8000-0000000000c1");
        var r1Id = Guid.Parse("019944af-0000-7000-8000-0000000000c2");
        var u2Id = Guid.Parse("019944af-0000-7000-8000-0000000000c3");
        var u3Id = Guid.Parse("019944af-0000-7000-8000-0000000000c4");
        var u1 = Entry(u1Id, 1, EntryStatus.Completed, "U1") with { Role = ConversationRole.User, ResponseId = null };
        var streaming = Entry(r1Id, 2, EntryStatus.Streaming, "R1");
        var u2 = Entry(u2Id, 3, EntryStatus.Completed, "U2") with { Role = ConversationRole.User, ResponseId = null };
        var u3 = Entry(u3Id, 4, EntryStatus.Completed, "U3") with { Role = ConversationRole.User, ResponseId = null };
        var first = First() with
        {
            Status = SessionStatus.Attached,
            Entries = [u1, streaming, u2, u3]
        };
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await opened.Store.EnsureCreatedAsync();
            await opened.Store.SaveAsync(first, 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        await reopened.Store.RecoverCrashedSessionsAsync();
        var loaded = await reopened.Store.LoadAsync(first.SessionId);
        Assert.Equal(SessionStatus.Paused, loaded!.Status);
        Assert.Equal(["U1", "U2", "U3"], loaded.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        Assert.Equal(EntryStatus.Interrupted, loaded.Entries.Single(entry => entry.Role == ConversationRole.Assistant).Status);
        Assert.Equal(["U2", "U3"], TrailingUserSuffix.Of(loaded.Entries).Select(entry => entry.Text).ToArray());
    }

    [Fact]
    public async Task Checkpoint_does_not_rewrite_unchanged_completed_rows()
    {
        await using var harness = await SqliteAsync();
        var completed = Entry(Guid.NewGuid(), 1, EntryStatus.Completed, "Done");
        var streaming = Entry(Guid.NewGuid(), 2, EntryStatus.Streaming, "Hi");
        var first = First() with { Revision = 1, Entries = [completed] };
        await harness.Store.SaveAsync(first, 0);
        harness.Store.EntryUpdates = 0;
        var second = first with
        {
            Revision = 2,
            Entries = [completed, streaming]
        };
        await harness.Store.SaveAsync(second, 1);
        Assert.Equal(1, harness.Store.EntryUpdates);
        var loaded = await harness.Store.LoadAsync(first.SessionId);
        Assert.Equal(completed.HeardTextEndExclusive, loaded!.Entries[0].HeardTextEndExclusive);
        Assert.Equal("Done", loaded.Entries[0].Text);
    }

    [Fact]
    public async Task Profile_allowlist_is_enforced_and_survives_sqlite_reopen()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        foreach (var store in stores)
        {
            var profile = new UserProfile(
                LocalUserProfile.Id,
                1,
                new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "Pat" },
                now);
            await store.SaveProfileAsync(profile, 0);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.SaveProfileAsync(profile with
                {
                    Revision = 2,
                    Preferences = new Dictionary<string, string> { ["theme"] = "dark" }
                }, 1).AsTask());
        }

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        try
        {
            await using (var opened = OpenSqlite(path, deleteOnDispose: false))
            {
                await opened.Store.EnsureCreatedAsync();
                await opened.Store.SaveProfileAsync(
                    new UserProfile(
                        LocalUserProfile.Id,
                        1,
                        new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "Pat" },
                        now),
                    0);
            }

            await using var reopened = OpenSqlite(path, deleteOnDispose: true);
            await reopened.Store.EnsureCreatedAsync();
            var loaded = await reopened.Store.LoadProfileAsync(LocalUserProfile.Id);
            Assert.Equal("Pat", loaded!.Preferences["preferredName"]);
        }
        finally
        {
            ReleaseSqlite(path);
        }
    }

    [Fact]
    public async Task Sqlite_migrate_reopens_legacy_ensurecreated_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await using var db = await opened.Factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            await opened.Store.SaveAsync(First(), 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        var loaded = await reopened.Store.LoadAsync(First().SessionId);
        Assert.Equal("examiner", loaded!.Definition.Id);
        Assert.Equal(1, loaded.Revision);
    }

    [Fact]
    public async Task Sqlite_rejects_partial_and_older_legacy_schema()
    {
        var partial = Path.Combine(Path.GetTempPath(), $"agent-core-partial-{Guid.NewGuid():N}.db");
        var older = Path.Combine(Path.GetTempPath(), $"agent-core-older-{Guid.NewGuid():N}.db");
        var missingIndex = Path.Combine(Path.GetTempPath(), $"agent-core-noidx-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={partial}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Sessions (SessionId TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            await using (var opened = OpenSqlite(partial, deleteOnDispose: false))
            {
                var error = await Assert.ThrowsAsync<AgentCoreException>(() => opened.Store.EnsureCreatedAsync().AsTask());
                Assert.Equal("SessionPersistenceUnavailable", error.Code);
            }

            await using (var connection = new SqliteConnection($"Data Source={older}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE Sessions (
                        SessionId TEXT PRIMARY KEY,
                        AgentId TEXT NOT NULL,
                        Status TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var opened = OpenSqlite(older, deleteOnDispose: false))
            {
                var error = await Assert.ThrowsAsync<AgentCoreException>(() => opened.Store.EnsureCreatedAsync().AsTask());
                Assert.Equal("SessionPersistenceUnavailable", error.Code);
            }

            await using (var connection = new SqliteConnection($"Data Source={missingIndex}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE Sessions (
                        SessionId TEXT NOT NULL PRIMARY KEY,
                        AgentId TEXT NOT NULL,
                        AgentVersion INTEGER NOT NULL,
                        DefinitionJson TEXT NOT NULL,
                        Mode TEXT NOT NULL,
                        PendingMode TEXT,
                        Status TEXT NOT NULL,
                        CreatedAtUtc INTEGER NOT NULL,
                        UpdatedAtUtc INTEGER NOT NULL,
                        Revision INTEGER NOT NULL
                    );
                    CREATE TABLE UserProfiles (
                        ProfileId TEXT NOT NULL PRIMARY KEY,
                        PreferencesJson TEXT NOT NULL,
                        Revision INTEGER NOT NULL,
                        UpdatedAtUtc INTEGER NOT NULL
                    );
                    CREATE TABLE ConversationEntries (
                        EntryId TEXT NOT NULL PRIMARY KEY,
                        SessionId TEXT NOT NULL,
                        EntrySequence INTEGER NOT NULL,
                        SourceEventId TEXT,
                        Role TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        ResponseId TEXT,
                        Status TEXT NOT NULL,
                        DeliveryMode TEXT NOT NULL,
                        HeardTextEndExclusive INTEGER NOT NULL,
                        ReceivedTextEndExclusive INTEGER NOT NULL,
                        CreatedAtUtc INTEGER NOT NULL,
                        FOREIGN KEY (SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                    );
                    CREATE TABLE SessionSnapshots (
                        SessionId TEXT NOT NULL PRIMARY KEY,
                        SchemaVersion INTEGER NOT NULL,
                        Summary TEXT NOT NULL,
                        SummarizedThroughEntrySequence INTEGER NOT NULL,
                        PendingTopic TEXT,
                        ProfileId TEXT,
                        LastEntrySequence INTEGER NOT NULL,
                        UpdatedAtUtc INTEGER NOT NULL,
                        FOREIGN KEY (SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var opened = OpenSqlite(missingIndex, deleteOnDispose: false))
            {
                var error = await Assert.ThrowsAsync<AgentCoreException>(() => opened.Store.EnsureCreatedAsync().AsTask());
                Assert.Equal("SessionPersistenceUnavailable", error.Code);
            }
        }
        finally
        {
            ReleaseSqlite(partial, older, missingIndex);
            foreach (var path in new[] { partial, older, missingIndex })
            {
                try
                {
                    File.Delete(path);
                    File.Delete(path + "-wal");
                    File.Delete(path + "-shm");
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Profile_unique_key_is_conflict_and_other_update_errors_are_unavailable()
    {
        await using var harness = await SqliteAsync();
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var profile = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "Pat" },
            now);
        var first = harness.Store.SaveProfileAsync(profile, 0).AsTask();
        var second = harness.Store.SaveProfileAsync(profile, 0).AsTask();
        var results = await Task.WhenAll(
            first.ContinueWith(task => task.Exception?.GetBaseException() as AgentCoreException),
            second.ContinueWith(task => task.Exception?.GetBaseException() as AgentCoreException));
        var conflict = results.Single(item => item is not null);
        Assert.Equal("Conflict", conflict!.Code);

        await using var db = await harness.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER profile_io AFTER UPDATE ON UserProfiles
            BEGIN
                SELECT RAISE(ABORT, 'disk I/O error');
            END;
            """);
        var unavailable = await Assert.ThrowsAsync<AgentCoreException>(() =>
            harness.Store.SaveProfileAsync(
                profile with { Revision = 2, Preferences = new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "Sam" } },
                1).AsTask());
        Assert.Equal("SessionPersistenceUnavailable", unavailable.Code);
    }

    [Fact]
    public async Task Session_unique_key_is_conflict_and_other_update_errors_are_unavailable()
    {
        await using var harness = await SqliteAsync();
        var snapshot = First();
        await harness.Store.SaveAsync(snapshot, 0);
        await using var db = await harness.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER session_io AFTER UPDATE ON Sessions
            BEGIN
                SELECT RAISE(ABORT, 'disk I/O error');
            END;
            """);
        var unavailable = await Assert.ThrowsAsync<AgentCoreException>(() =>
            harness.Store.SaveAsync(
                snapshot with { Revision = 2, Status = SessionStatus.Attached },
                1).AsTask());
        Assert.Equal("SessionPersistenceUnavailable", unavailable.Code);
    }

    [Fact]
    public async Task Sqlite_migrate_reopens_existing_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await opened.Store.EnsureCreatedAsync();
            await opened.Store.SaveAsync(First(), 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        var loaded = await reopened.Store.LoadAsync(First().SessionId);
        Assert.Equal("examiner", loaded!.Definition.Id);
        Assert.Equal(1, loaded.Revision);
    }

    [Fact]
    public async Task Duplicate_source_event_retry_upserts_without_a_second_row()
    {
        await using var harness = await SqliteAsync();
        var source = Guid.Parse("019944af-0000-7000-8000-000000000010");
        var entry = Entry(source, 1, EntryStatus.Completed, "Hello") with { SourceEventId = source };
        var first = First() with { Entries = [entry] };
        await harness.Store.SaveAsync(first, 0);
        var retry = first with { Revision = 2, Summary = "retry" };
        await harness.Store.SaveAsync(retry, 1);
        var loaded = await harness.Store.LoadAsync(first.SessionId);
        Assert.Single(loaded!.Entries);
        Assert.Equal(source, loaded.Entries[0].SourceEventId);
        Assert.Equal("retry", loaded.Summary);
    }

    [Fact]
    public async Task Clean_and_failed_end_persist_terminal_status()
    {
        await using var harness = await SqliteAsync();
        var failed = Entry(Guid.NewGuid(), 1, EntryStatus.Failed, "oops");
        var clean = First() with { Revision = 1, Status = SessionStatus.Ended, PendingMode = null };
        await harness.Store.SaveAsync(clean, 0);
        var loaded = await harness.Store.LoadAsync(clean.SessionId);
        Assert.Equal(SessionStatus.Ended, loaded!.Status);
        Assert.Null(loaded.PendingMode);

        var other = First() with
        {
            SessionId = Guid.Parse("019944af-0000-7000-8000-000000000099"),
            Entries = [failed]
        };
        await harness.Store.SaveAsync(other, 0);
        var ended = other with { Revision = 2, Status = SessionStatus.Ended };
        await harness.Store.SaveAsync(ended, 1);
        loaded = await harness.Store.LoadAsync(other.SessionId);
        Assert.Equal(SessionStatus.Ended, loaded!.Status);
        Assert.Equal(EntryStatus.Failed, loaded.Entries[0].Status);
    }

    [Fact]
    public async Task Pending_voice_clears_on_pause_recovery()
    {
        await using var harness = await SqliteAsync();
        var snapshot = First() with { Status = SessionStatus.Paused, PendingMode = null };
        await harness.Store.SaveAsync(snapshot, 0);
        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        Assert.Null(loaded!.PendingMode);
        Assert.Equal(SessionStatus.Paused, loaded.Status);
    }

    [Fact]
    public async Task Backup_restore_reopens_the_session()
    {
        var source = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var backup = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}-bak.db");
        try
        {
            await using (var opened = OpenSqlite(source, deleteOnDispose: false))
            {
                await opened.Store.EnsureCreatedAsync();
                await opened.Store.SaveAsync(First(), 0);
                await opened.Store.BackupToAsync(backup);
            }

            await using var restored = OpenSqlite(backup, deleteOnDispose: true);
            var loaded = await restored.Store.LoadAsync(First().SessionId);
            Assert.Equal("examiner", loaded!.Definition.Id);
            Assert.Equal(1, loaded.Revision);
        }
        finally
        {
            ReleaseSqlite(source, backup);
            foreach (var path in new[] { source, source + "-wal", source + "-shm", backup, backup + "-wal", backup + "-shm" })
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
    }

    private static void ReleaseSqlite(params string[] paths)
    {
        foreach (var path in paths)
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
        }
    }

    private static async Task<SqliteHarness> SqliteAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var harness = OpenSqlite(path, deleteOnDispose: true);
        await harness.Store.EnsureCreatedAsync();
        return harness;
    }

    [Fact]
    public async Task Fresh_sqlite_file_migrates_with_wal_interceptor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-wal-{Guid.NewGuid():N}.db");
        var interceptor = new SqlitePragmaInterceptor(5000);
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(interceptor)
            .Options;
        var store = new SqliteMemoryStore(new TestFactory(options), new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)));
        try
        {
            await store.EnsureCreatedAsync();
            var snapshot = First();
            await store.SaveAsync(snapshot, 0);
            var loaded = await store.LoadAsync(snapshot.SessionId);
            Assert.NotNull(loaded);
        }
        finally
        {
            ReleaseSqlite(path);
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static SqliteHarness OpenSqlite(string path, bool deleteOnDispose)
    {
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new TestFactory(options);
        var store = new SqliteMemoryStore(factory, new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)));
        return new SqliteHarness(path, factory, store, deleteOnDispose);
    }

    private static SessionSnapshot First() =>
        new(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
            1,
            Definition(),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Envelope_and_block_delivery_round_trip_sqlite()
    {
        await using var harness = await SqliteAsync();
        var envelope = new ResponseEnvelope(
            "Hello",
            "Spoken",
            [
                new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "**Hi**", null, null, true)
            ]);
        var entry = Entry(Guid.NewGuid(), 1, EntryStatus.Completed, "Hello") with
        {
            Envelope = envelope,
            HeardTextEndExclusive = 3,
            ReceivedTextEndExclusive = 5
        };
        var snapshot = First() with { Entries = [entry] };
        await harness.Store.SaveAsync(snapshot, 0);
        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        var restored = Assert.Single(loaded!.Entries);
        Assert.Equal("Hello", restored.Text);
        Assert.Equal("Spoken", restored.Envelope!.SpeechText);
        Assert.True(Assert.Single(restored.Envelope.Blocks).DisplayDelivered);
        Assert.Equal(3, restored.HeardTextEndExclusive);
        Assert.Equal(5, restored.ReceivedTextEndExclusive);
    }

    private static ConversationEntry Entry(Guid id, long sequence, EntryStatus status, string text) =>
        new(
            id,
            sequence,
            id,
            ConversationRole.Assistant,
            text,
            id,
            status,
            SessionMode.Text,
            Math.Min(3, text.Length),
            text.Length,
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static AgentDefinition Definition() =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SqliteHarness(
        string path,
        IDbContextFactory<AgentCoreDbContext> factory,
        SqliteMemoryStore store,
        bool deleteOnDispose) : IAsyncDisposable
    {
        public IDbContextFactory<AgentCoreDbContext> Factory { get; } = factory;
        public SqliteMemoryStore Store { get; } = store;

        public ValueTask DisposeAsync()
        {
            ReleaseSqlite(path);
            if (deleteOnDispose)
            {
                try
                {
                    File.Delete(path);
                    File.Delete(path + "-wal");
                    File.Delete(path + "-shm");
                }
                catch (IOException)
                {
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
