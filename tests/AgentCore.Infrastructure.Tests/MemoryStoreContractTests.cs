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
    public async Task Long_transcript_metadata_restore_and_history_page_do_not_materialize_all_rows()
    {
        await using var harness = await SqliteAsync();
        var entries = LongTranscript(250, trailingUsers: 35);
        var first = First() with { Entries = entries };
        await harness.Store.SaveAsync(first, 0);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            Assert.Equal(250, await db.Entries.CountAsync());
        }

        var meta = await harness.Store.LoadMetadataAsync(first.SessionId);
        Assert.Equal(250, meta!.DurableLastEntrySequence);
        Assert.Empty(meta.Entries);
        Assert.Equal(0, harness.Store.MaterializedEntryRows);

        var restored = await harness.Store.LoadAsync(first.SessionId);
        Assert.Equal(250, restored!.DurableLastEntrySequence);
        Assert.True(restored.Entries.Count < 250);
        Assert.Equal(35, TrailingUserSuffix.Of(restored.Entries).Count);
        Assert.Contains(restored.Entries, entry => entry.Sequence == 250);
        Assert.DoesNotContain(restored.Entries, entry => entry.Sequence == 1);
        Assert.True(harness.Store.MaterializedEntryRows < 250);

        var page = await harness.Store.ReadHistoryAsync(first.SessionId, 200, 50);
        Assert.Equal(50, page.Count);
        Assert.Equal(50, harness.Store.MaterializedEntryRows);

        var window = restored with { Revision = 2, Summary = "bounded-save" };
        await harness.Store.SaveAsync(window, 1);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var sequences = await db.Entries.OrderBy(entry => entry.EntrySequence)
                .Select(entry => entry.EntrySequence)
                .ToArrayAsync();
            Assert.Equal(250, sequences.Length);
            Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), sequences);
        }

        var afterSave = await harness.Store.LoadMetadataAsync(first.SessionId);
        Assert.Equal(250, afterSave!.LastEntrySequence);
        Assert.Equal("bounded-save", afterSave.Summary);
    }

    [Fact]
    public async Task Recover_on_long_transcript_does_not_materialize_every_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var entries = LongTranscript(220, trailingUsers: 4).ToArray();
        entries[215] = entries[215] with { Status = EntryStatus.Streaming, Role = ConversationRole.Assistant };
        var first = First() with { Status = SessionStatus.Attached, Entries = entries };
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await opened.Store.EnsureCreatedAsync();
            await opened.Store.SaveAsync(first, 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        await reopened.Store.RecoverCrashedSessionsAsync();
        Assert.True(reopened.Store.MaterializedEntryRows < 220);
        var loaded = await reopened.Store.LoadAsync(first.SessionId);
        Assert.Equal(SessionStatus.Paused, loaded!.Status);
        Assert.Equal(220, loaded.DurableLastEntrySequence);
        Assert.Equal(EntryStatus.Interrupted, loaded.Entries.Single(entry => entry.Sequence == 216).Status);
    }

    [Fact]
    public async Task In_memory_bounded_save_retains_older_entries()
    {
        var store = new InMemoryMemoryStore();
        var entries = LongTranscript(80, trailingUsers: 5);
        await store.SaveAsync(First() with { Entries = entries }, 0);
        var restored = await store.LoadAsync(First().SessionId);
        Assert.True(restored!.Entries.Count < 80);
        await store.SaveAsync(restored with { Revision = 2 }, 1);
        var page = await store.ReadHistoryAsync(First().SessionId, 0, 80);
        Assert.Equal(80, page.Count);
        Assert.Equal(80, (await store.LoadMetadataAsync(First().SessionId))!.LastEntrySequence);
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
                new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                {
                    ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                    ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
                },
                now);
            await store.SaveProfileAsync(profile, 0);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.SaveProfileAsync(profile with
                {
                    Revision = 2,
                    Preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                    {
                        ["theme"] = LocalUserProfile.ApplicationProfileValue("dark", now)
                    }
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
                        new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                        {
                            ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                            ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
                        },
                        now),
                    0);
            }

            await using var reopened = OpenSqlite(path, deleteOnDispose: true);
            await reopened.Store.EnsureCreatedAsync();
            var loaded = await reopened.Store.LoadProfileAsync(LocalUserProfile.Id);
            Assert.Equal("Pat", loaded!.Preferences["preferredName"].Value);
            Assert.Equal(UserProfileValueSource.UserSet, loaded.Preferences["preferredName"].Source);
        }
        finally
        {
            ReleaseSqlite(path);
        }
    }

    [Fact]
    public async Task Profile_legacy_json_loads_and_next_save_rewrites_typed_json()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        await using var harness = await SqliteAsync();
        await harness.Store.SaveProfileAsync(
            new UserProfile(
                LocalUserProfile.Id,
                1,
                LocalUserProfile.CreateDefaultSeed(now),
                now),
            0);

        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.Profiles.SingleAsync(item => item.ProfileId == LocalUserProfile.Id.ToString("D"));
            row.PreferencesJson = """{"language":"en","preferredName":"friend"}""";
            await db.SaveChangesAsync();
        }

        var loaded = await harness.Store.LoadProfileAsync(LocalUserProfile.Id);
        Assert.NotNull(loaded);
        Assert.False(loaded!.Preferences.ContainsKey("preferredName"));
        Assert.Equal(UserProfileValueSource.ApplicationProfile, loaded.Preferences["language"].Source);

        var rewritten = loaded with { Revision = 2, UpdatedAt = now.AddHours(1) };
        await harness.Store.SaveProfileAsync(rewritten, 1);

        await using var verify = await harness.Factory.CreateDbContextAsync();
        var persisted = await verify.Profiles.AsNoTracking()
            .SingleAsync(item => item.ProfileId == LocalUserProfile.Id.ToString("D"));
        Assert.Contains("applicationProfile", persisted.PreferencesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"language\":\"en\"", persisted.PreferencesJson, StringComparison.Ordinal);
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
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
            },
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
                profile with
                {
                    Revision = 2,
                    Preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                    {
                        ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                        ["preferredName"] = new UserProfileValue("Sam", UserProfileValueSource.UserSet, now)
                    }
                },
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
    public async Task Ended_and_archived_sessions_keep_orthogonal_lifecycle()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        var archivedAt = new DateTimeOffset(2026, 9, 19, 4, 0, 0, TimeSpan.Zero);
        foreach (var store in stores)
        {
            var ended = First() with
            {
                SessionId = Guid.NewGuid(),
                Status = SessionStatus.Ended
            };
            await store.SaveAsync(ended, 0);
            var loaded = await store.LoadMetadataAsync(ended.SessionId);
            Assert.Equal(SessionStatus.Ended, loaded!.Status);
            Assert.Equal(SessionLifecycleStatus.Ended, loaded.LifecycleStatus);
            Assert.Null(loaded.ArchivedAt);

            var archived = loaded with { Revision = 2, ArchivedAt = archivedAt };
            await store.SaveAsync(archived, 1);
            loaded = await store.LoadMetadataAsync(ended.SessionId);
            Assert.Equal(SessionLifecycleStatus.Ended, loaded!.LifecycleStatus);
            Assert.Equal(archivedAt, loaded.ArchivedAt);
        }
    }

    [Fact]
    public async Task Ending_recovery_is_deterministically_ended()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        foreach (var store in stores)
        {
            var ending = First() with
            {
                SessionId = Guid.NewGuid(),
                Status = SessionStatus.Ending
            };
            await store.SaveAsync(ending, 0);
            await store.RecoverCrashedSessionsAsync();
            var loaded = await store.LoadMetadataAsync(ending.SessionId);
            Assert.Equal(SessionStatus.Ended, loaded!.Status);
            Assert.Equal(SessionLifecycleStatus.Ended, loaded.LifecycleStatus);
            Assert.Equal("ending-recovery", loaded.LifecycleReason);
            Assert.Equal(LifecycleTransitionSource.System, loaded.LifecycleSource);
        }
    }

    [Fact]
    public async Task Speech_locale_override_round_trips_without_changing_definition_language()
    {
        await using var harness = await SqliteAsync();
        IMemoryStore[] stores = [new InMemoryMemoryStore(), harness.Store];
        foreach (var store in stores)
        {
            var snapshot = First() with { SpeechLocaleOverride = "vi-VN" };
            await store.SaveAsync(snapshot, 0);
            var loaded = await store.LoadMetadataAsync(snapshot.SessionId);
            Assert.Equal("vi-VN", loaded!.SpeechLocaleOverride);
            Assert.Equal(snapshot.Definition.ConversationPolicy.Language, loaded.Definition.ConversationPolicy.Language);
        }
    }

    [Fact]
    public async Task Purpose_policy_and_private_metadata_round_trip()
    {
        await using var harness = await SqliteAsync();
        var deadline = new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero);
        var snapshot = First() with
        {
            Purpose = new SessionPurpose(
                SessionPurposeKind.Goal,
                "Host exam window",
                deadline,
                new Dictionary<string, string> { ["integration"] = "exam-app" }),
            CompletionPolicy = new SessionCompletionPolicy(
                AgentCompletionAuthority.Advisory,
                UserCompletionAllowed: false,
                UserCancellationAllowed: true),
            LifecycleStatus = SessionLifecycleStatus.Active,
            LifecycleSource = LifecycleTransitionSource.Host,
            LifecycleReason = "created",
            LifecycleChangedAt = deadline.AddHours(-1)
        };
        await harness.Store.SaveAsync(snapshot, 0);
        var loaded = await harness.Store.LoadMetadataAsync(snapshot.SessionId);
        Assert.Equal(SessionPurposeKind.Goal, loaded!.Purpose!.Kind);
        Assert.Equal("Host exam window", loaded.Purpose.Description);
        Assert.Equal(deadline, loaded.Purpose.DeadlineAt);
        Assert.Equal("exam-app", loaded.Purpose.Metadata!["integration"]);
        Assert.Equal(AgentCompletionAuthority.Advisory, loaded.CompletionPolicy!.AgentCompletion);
        Assert.False(loaded.CompletionPolicy.UserCompletionAllowed);
        Assert.True(loaded.CompletionPolicy.UserCancellationAllowed);
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
        Assert.Equal(ResponseSpeechMode.Custom, restored.Envelope.SpeechMode);
        Assert.True(Assert.Single(restored.Envelope.Blocks).DisplayDelivered);
        Assert.Equal(3, restored.HeardTextEndExclusive);
        Assert.Equal(5, restored.ReceivedTextEndExclusive);
    }

    [Fact]
    public async Task Same_mode_derived_speech_survives_sqlite_reopen()
    {
        await using var harness = await SqliteAsync();
        const string display = "The architecture has **three** pieces.";
        const string spoken = "The architecture has three pieces.";
        var envelope = new ResponseEnvelope(display, spoken, [], ResponseSpeechMode.Same);
        var entry = Entry(Guid.NewGuid(), 1, EntryStatus.Completed, display) with
        {
            Envelope = envelope,
            HeardTextEndExclusive = spoken.Length
        };
        var snapshot = First() with { Entries = [entry] };
        await harness.Store.SaveAsync(snapshot, 0);
        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        var restored = Assert.Single(loaded!.Entries);
        Assert.Equal(ResponseSpeechMode.Same, restored.Envelope!.SpeechMode);
        Assert.Equal(spoken, restored.Envelope.SpeechText);
        Assert.Equal(spoken.Length, restored.HeardTextEndExclusive);
    }

    [Fact]
    public async Task Legacy_speech_text_envelope_json_reopens_as_custom_and_is_not_rewritten_when_unchanged()
    {
        await using var harness = await SqliteAsync();
        const string legacy =
            """{"displayText":"Hello","speechText":"Spoken","blocks":[{"blockId":"b1","kind":0,"displayText":"**Hi**","fallbackText":"**Hi**","attachmentId":null,"artifactId":null,"displayDelivered":true}]}""";
        var decoded = ResponseEnvelopeJson.Deserialize(legacy)!;
        var entry = Entry(Guid.NewGuid(), 1, EntryStatus.Completed, "Hello") with { Envelope = decoded };
        var snapshot = First() with { Entries = [entry] };
        await harness.Store.SaveAsync(snapshot, 0);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = Assert.Single(db.Entries);
            row.EnvelopeJson = legacy;
            await db.SaveChangesAsync();
        }

        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        var restored = Assert.Single(loaded!.Entries);
        Assert.Equal(ResponseSpeechMode.Custom, restored.Envelope!.SpeechMode);
        Assert.Equal("Spoken", restored.Envelope.SpeechText);
        await harness.Store.SaveAsync(loaded with { Revision = loaded.Revision + 1 }, loaded.Revision);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            Assert.Equal(legacy, Assert.Single(db.Entries).EnvelopeJson);
        }
    }

    [Fact]
    public async Task Session_model_selection_and_turn_provenance_survive_sqlite_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-{Guid.NewGuid():N}.db");
        var selection = new SessionModelSelection(
            "scripted-alpha",
            "primary-llm",
            "scripted-alpha",
            ModelSelectionSource.SystemDefault,
            "medium");
        var entry = Entry(Guid.NewGuid(), 1, EntryStatus.Completed, "Hello") with
        {
            ModelProvenance = new ModelGenerationProvenance(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                "medium")
        };
        var snapshot = First() with { Entries = [entry], ModelSelection = selection };
        await using (var opened = OpenSqlite(path, deleteOnDispose: false))
        {
            await opened.Store.EnsureCreatedAsync();
            await opened.Store.SaveAsync(snapshot, 0);
        }

        await using var reopened = OpenSqlite(path, deleteOnDispose: true);
        await reopened.Store.EnsureCreatedAsync();
        var restored = await reopened.Store.LoadAsync(snapshot.SessionId);
        Assert.Equal(selection, restored!.ModelSelection);
        Assert.Equal(entry.ModelProvenance, Assert.Single(restored.Entries).ModelProvenance);
    }

    private static IReadOnlyList<ConversationEntry> LongTranscript(int count, int trailingUsers)
    {
        var entries = new ConversationEntry[count];
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var id = Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}");
            var trailing = sequence > count - trailingUsers;
            entries[sequence - 1] = Entry(
                    id,
                    sequence,
                    EntryStatus.Completed,
                    trailing ? $"U{sequence}" : $"A{sequence}") with
                {
                    Role = trailing ? ConversationRole.User : ConversationRole.Assistant,
                    ResponseId = trailing ? null : id
                };
        }

        return entries;
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
