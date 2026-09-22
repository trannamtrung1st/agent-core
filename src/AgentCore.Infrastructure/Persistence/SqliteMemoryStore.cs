using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteMemoryStore(IDbContextFactory<AgentCoreDbContext> contexts, TimeProvider time) : IMemoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public int EntryUpdates { get; set; }

    public int MaterializedEntryRows { get; private set; }

    public int LoadAsyncCalls { get; private set; }

    public int LoadMetadataCalls { get; private set; }

    public async ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await StampLegacyEnsureCreatedAsync(db, cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StampLegacyEnsureCreatedAsync(AgentCoreDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var tables = connection.CreateCommand();
        tables.CommandText =
            """
            SELECT
                EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Sessions'),
                EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory')
            """;
        await using (var reader = await tables.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var hasSessions = reader.GetInt64(0) != 0;
            var hasHistory = reader.GetInt64(1) != 0;
            if (!hasSessions || hasHistory)
            {
                return;
            }
        }

        if (await HasCompleteLegacySchemaAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260915064647_InitialCreate', '10.0.12');
                """,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await HasEnsureCreatedCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            var attachments = await TableExistsAsync(connection, "Attachments", cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260915064647_InitialCreate', '10.0.12');
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260916104118_SessionCatalogAndOwnerCapability', '10.0.12');
                """,
                cancellationToken).ConfigureAwait(false);
            if (attachments)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260916120000_Attachments', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "ConversationEntries", "EnvelopeJson", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260916180000_ResponseEnvelope', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "ConversationEntries", "AttachmentRefsJson", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260917012822_ConversationEntryAttachmentRefs', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "SessionSnapshots", "LastUserActivityAtUtc", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260917100000_LastUserActivityAt', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "Sessions", "PauseReason", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260917120000_PauseReasonAndActivityBackfill', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "ConversationEntries", "SourceAdmissionFingerprint", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260917234608_SourceAdmissionFingerprint', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "ConversationEntries", "FinishReason", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260918013000_EntryFinishReason', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "ConversationEntries", "InterruptReason", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260922103000_EntryInterruptReason', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "SessionSnapshots", "SummaryFormatVersion", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260923025000_SummaryMetadata', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(connection, "StructuredMemories", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260923050000_StructuredSessionMemory', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(connection, "AgentInstances", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260923070000_AgentInstance', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "StructuredMemories", "Scope", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260923090000_MemoryScope', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "SessionSnapshots", "LifecycleStatus", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260918200644_SessionLifecycle', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "SessionSnapshots", "SpeechLocaleOverride", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260919040000_SpeechLocaleOverride', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await ColumnExistsAsync(connection, "SessionSnapshots", "ModelCatalogKey", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260919080000_SessionModelSelection', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(connection, "Artifacts", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260916210000_Artifacts', '10.0.12');
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        throw AgentCoreErrors.Persistence("Legacy SQLite schema is incomplete.");
    }

    public async ValueTask BackupToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationPath}");
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        await BackupDatabaseWithRetryAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupDatabaseWithRetryAsync(
        SqliteConnection source,
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsBusyOrLocked(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6;

    public async ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        LoadAsyncCalls++;
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var row = await db.Sessions
            .AsNoTracking()
            .Include(item => item.Snapshot)
            .SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var entries = await LoadRestoreEntryRowsAsync(db, key, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(row, entries);
    }

    public async ValueTask<SessionSnapshot?> LoadMetadataAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LoadMetadataCalls++;
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var row = await db.Sessions
            .AsNoTracking()
            .Include(item => item.Snapshot)
            .SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSnapshot(row, []);
    }

    public async ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = snapshot.SessionId.ToString("D");
        var existing = await db.Sessions
            .Include(item => item.Snapshot)
            .SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            if (expectedRevision != 0 || snapshot.Revision != 1)
            {
                throw AgentCoreErrors.Conflict("Insert requires expectedRevision 0 and snapshot.Revision 1.");
            }

            db.Sessions.Add(ToRecord(snapshot));
            MaterializedEntryRows += snapshot.Entries.Count;
            await SaveChangesOrThrowAsync(db, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var matched = await LoadMatchingEntryRowsAsync(db, key, snapshot.Entries, cancellationToken)
            .ConfigureAwait(false);
        var current = ToSnapshot(existing, matched);
        if (existing.Revision == snapshot.Revision && MemoryStoreSemantics.SameContent(current, snapshot))
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (existing.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1)
        {
            throw AgentCoreErrors.Conflict("Stale session revision.");
        }

        ApplySession(existing, snapshot);
        await UpsertEntriesAsync(db, existing, snapshot, cancellationToken).ConfigureAwait(false);
        await SaveChangesOrThrowAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var rows = await db.Entries.AsNoTracking()
            .Where(entry => entry.SessionId == key && entry.EntrySequence > afterEntrySequence)
            .OrderBy(entry => entry.EntrySequence)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        MaterializedEntryRows += rows.Length;
        return rows.Select(ToEntry).ToArray();
    }

    public async ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
        Guid sessionId,
        long? afterEntrySequence,
        long? beforeEntrySequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var exists = await db.Sessions.AsNoTracking()
            .AnyAsync(row => row.SessionId == key && row.DurablyDeletedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        if (afterEntrySequence is { } after)
        {
            var forward = await db.Entries.AsNoTracking()
                .Where(entry => entry.SessionId == key && entry.EntrySequence > after)
                .OrderBy(entry => entry.EntrySequence)
                .Take(limit)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            MaterializedEntryRows += forward.Length;
            return HistoryPaging.FromForward(forward.Select(ToEntry).ToArray(), after, limit);
        }

        var query = db.Entries.AsNoTracking().Where(entry => entry.SessionId == key);
        if (beforeEntrySequence is { } before)
        {
            query = query.Where(entry => entry.EntrySequence < before);
        }

        var newestFirst = await query
            .OrderByDescending(entry => entry.EntrySequence)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        MaterializedEntryRows += newestFirst.Length;
        return HistoryPaging.FromNewestFirst(newestFirst.Select(ToEntry).ToArray(), limit);
    }

    public async ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Profiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProfileId == profileId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var preferences = UserProfilePreferencesCodec.Read(row.PreferencesJson, FromUnix(row.UpdatedAtUtc));
        return new UserProfile(profileId, row.Revision, preferences, FromUnix(row.UpdatedAtUtc));
    }

    public async ValueTask SaveProfileAsync(
        UserProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        LocalUserProfile.Validate(profile.Preferences);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = profile.ProfileId.ToString("D");
        var existing = await db.Profiles.SingleOrDefaultAsync(item => item.ProfileId == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            if (expectedRevision != 0)
            {
                throw AgentCoreErrors.Conflict("Insert profile requires expectedRevision 0.");
            }

            db.Profiles.Add(new ProfileRecord
            {
                ProfileId = key,
                PreferencesJson = UserProfilePreferencesCodec.Write(profile.Preferences),
                Revision = profile.Revision,
                UpdatedAtUtc = profile.UpdatedAt.ToUnixTimeMilliseconds()
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueKeyViolation(ex))
            {
                throw AgentCoreErrors.Conflict("Profile already exists.");
            }
            catch (DbUpdateException)
            {
                throw AgentCoreErrors.Persistence("Persistent profile save failed.");
            }

            return;
        }

        if (existing.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Stale profile revision.");
        }

        existing.PreferencesJson = UserProfilePreferencesCodec.Write(profile.Preferences);
        existing.Revision = profile.Revision;
        existing.UpdatedAtUtc = profile.UpdatedAt.ToUnixTimeMilliseconds();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueKeyViolation(ex))
        {
            throw AgentCoreErrors.Conflict("Profile revision conflict.");
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Persistence("Persistent profile save failed.");
        }
    }

    public async ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default)
    {
        MaterializedEntryRows = 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Sessions.Include(item => item.Snapshot)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            if (row.DurablyDeletedAtUtc is not null)
            {
                continue;
            }

            var streaming = await db.Entries
                .Where(entry => entry.SessionId == row.SessionId && entry.Status == nameof(EntryStatus.Streaming))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            MaterializedEntryRows += streaming.Count;
            var recovered = MemoryStoreSemantics.Recover(ToSnapshot(row, streaming), now);
            if (recovered.Revision == row.Revision)
            {
                continue;
            }

            ApplySession(row, recovered);
            await UpsertEntriesAsync(db, row, recovered, cancellationToken).ConfigureAwait(false);
        }

        await SaveChangesOrThrowAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionCatalogPage> ListCatalogAsync(
        string? cursor,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw AgentCoreErrors.Validation("Catalog limit must be between 1 and 100.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Sessions.AsNoTracking().Include(item => item.Snapshot)
            .Where(row => row.DurablyDeletedAtUtc == null);
        if (!includeArchived)
        {
            query = query.Where(row => row.ArchivedAtUtc == null);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            var (ms, id) = CatalogCursor.Decode(cursor);
            var key = id.ToString("D");
            query = query.Where(row =>
                row.UpdatedAtUtc < ms
                || (row.UpdatedAtUtc == ms && string.CompareOrdinal(row.SessionId, key) < 0));
        }

        var rows = await query
            .OrderByDescending(row => row.UpdatedAtUtc)
            .ThenByDescending(row => row.SessionId)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Length > limit;
        var page = (hasMore ? rows.Take(limit) : rows)
            .Select(row => ToSnapshot(row, []))
            .ToArray();
        var next = hasMore ? CatalogCursor.Encode(page[^1].UpdatedAt, page[^1].SessionId) : null;
        return new SessionCatalogPage(page, next, hasMore);
    }

    public async ValueTask<IReadOnlyList<SessionSnapshot>> ListMissingInstanceAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Sessions.AsNoTracking().Include(item => item.Snapshot)
            .Where(row => row.AgentInstanceId == null)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => ToSnapshot(row, [])).ToArray();
    }

    public async ValueTask AssignInstanceAsync(
        Guid sessionId,
        Guid instanceId,
        AgentIdentity persona,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var row = await db.Sessions.SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        var changed = false;
        if (string.IsNullOrEmpty(row.AgentInstanceId))
        {
            row.AgentInstanceId = instanceId.ToString("D");
            changed = true;
        }

        if (string.IsNullOrEmpty(row.PinnedPersonaJson))
        {
            row.PinnedPersonaJson = JsonSerializer.Serialize(persona, Json);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<EntryRecord>> LoadRestoreEntryRowsAsync(
        AgentCoreDbContext db,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var newest = await db.Entries.AsNoTracking()
            .Where(entry => entry.SessionId == sessionId)
            .OrderByDescending(entry => entry.EntrySequence)
            .Take(HistoryRestoreWindow.PromptKeep)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        MaterializedEntryRows += newest.Count;
        newest.Reverse();
        while (newest.Count > 0)
        {
            var mapped = newest.Select(ToEntry).ToArray();
            var suffix = TrailingUserSuffix.Of(mapped);
            if (suffix.Count < mapped.Length)
            {
                break;
            }

            var oldest = newest[0].EntrySequence;
            var more = await db.Entries.AsNoTracking()
                .Where(entry => entry.SessionId == sessionId && entry.EntrySequence < oldest)
                .OrderByDescending(entry => entry.EntrySequence)
                .Take(50)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            MaterializedEntryRows += more.Count;
            if (more.Count == 0)
            {
                break;
            }

            more.Reverse();
            more.AddRange(newest);
            newest = more;
        }

        var loadedIds = newest.Select(entry => entry.EntryId).ToHashSet(StringComparer.Ordinal);
        var streaming = await db.Entries.AsNoTracking()
            .Where(entry => entry.SessionId == sessionId && entry.Status == nameof(EntryStatus.Streaming))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in streaming)
        {
            if (loadedIds.Add(row.EntryId))
            {
                MaterializedEntryRows++;
                newest.Add(row);
            }
        }

        return newest;
    }

    private async Task<List<EntryRecord>> LoadMatchingEntryRowsAsync(
        AgentCoreDbContext db,
        string sessionId,
        IReadOnlyList<ConversationEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var ids = entries.Select(entry => entry.EntryId.ToString("D")).ToArray();
        var rows = await db.Entries
            .Where(entry => entry.SessionId == sessionId && ids.Contains(entry.EntryId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        MaterializedEntryRows += rows.Count;
        return rows;
    }

    private async Task UpsertEntriesAsync(
        AgentCoreDbContext db,
        SessionRecord session,
        SessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Entries.Count == 0)
        {
            return;
        }

        var ids = snapshot.Entries.Select(entry => entry.EntryId.ToString("D")).ToArray();
        var existingRows = await db.Entries
            .Where(entry => entry.SessionId == session.SessionId && ids.Contains(entry.EntryId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        MaterializedEntryRows += existingRows.Count;
        var existing = existingRows.ToDictionary(entry => entry.EntryId, StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            var key = entry.EntryId.ToString("D");
            if (!existing.TryGetValue(key, out var row))
            {
                session.Entries.Add(ToRecord(session.SessionId, entry));
                EntryUpdates++;
                continue;
            }

            if (entry.Status != EntryStatus.Streaming && Unchanged(row, entry))
            {
                continue;
            }

            ApplyEntry(row, entry);
            EntryUpdates++;
        }
    }

    private static bool Unchanged(EntryRecord row, ConversationEntry entry) =>
        row.Text == entry.Text
        && row.Status == entry.Status.ToString()
        && row.HeardTextEndExclusive == entry.HeardTextEndExclusive
        && row.ReceivedTextEndExclusive == entry.ReceivedTextEndExclusive
        && EnvelopeUnchanged(row.EnvelopeJson, entry.Envelope)
        && row.AttachmentRefsJson == SerializeAttachmentRefs(entry.Attachments)
        && row.SourceAdmissionFingerprint == entry.SourceAdmissionFingerprint
        && row.FinishReason == entry.FinishReason
        && row.InterruptReason == entry.InterruptReason
        && row.Role == entry.Role.ToString()
        && row.DeliveryMode == entry.DeliveryMode.ToString()
        && row.ResponseId == entry.ResponseId?.ToString("D")
        && row.SourceEventId == entry.SourceEventId?.ToString("D")
        && row.EntrySequence == entry.Sequence;

    private static void ApplySession(SessionRecord row, SessionSnapshot snapshot)
    {
        row.AgentId = snapshot.Definition.Id;
        row.AgentVersion = snapshot.Definition.Version;
        row.DefinitionJson = JsonSerializer.Serialize(snapshot.Definition, Json);
        row.AgentInstanceId = snapshot.AgentInstanceId?.ToString("D");
        row.PinnedPersonaJson = snapshot.PinnedPersona is null
            ? null
            : JsonSerializer.Serialize(snapshot.PinnedPersona, Json);
        row.Mode = snapshot.Mode.ToString();
        row.PendingMode = snapshot.PendingMode?.ToString();
        row.Status = snapshot.Status.ToString();
        row.PauseReason = snapshot.PauseReason;
        row.Title = snapshot.Title;
        row.RuntimeEpoch = snapshot.RuntimeEpoch;
        row.WorkspaceOwned = snapshot.WorkspaceOwned;
        row.ArchivedAtUtc = snapshot.ArchivedAt?.ToUnixTimeMilliseconds();
        row.DurablyDeletedAtUtc = snapshot.DurablyDeletedAt?.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
        row.Revision = snapshot.Revision;
        row.Snapshot ??= new SnapshotRecord { SessionId = row.SessionId };
        row.Snapshot.SchemaVersion = snapshot.SchemaVersion;
        row.Snapshot.Summary = snapshot.Summary;
        row.Snapshot.SummarizedThroughEntrySequence = snapshot.SummarizedThroughEntrySequence;
        row.Snapshot.SummaryFormatVersion = snapshot.SummaryFormatVersion;
        row.Snapshot.SummaryGeneratedAtUtc = snapshot.SummaryGeneratedAt?.ToUnixTimeMilliseconds();
        row.Snapshot.SummaryModelCatalogKey = snapshot.SummaryModel?.CatalogKey;
        row.Snapshot.SummaryModelProviderAlias = snapshot.SummaryModel?.ProviderAlias;
        row.Snapshot.SummaryModelId = snapshot.SummaryModel?.ModelId;
        row.Snapshot.SummaryModelReasoningEffort = snapshot.SummaryModel?.ReasoningEffort;
        row.Snapshot.PendingTopic = snapshot.PendingTopic;
        row.Snapshot.ProfileId = snapshot.ProfileId?.ToString("D");
        row.Snapshot.LastEntrySequence = snapshot.DurableLastEntrySequence;
        row.Snapshot.UpdatedAtUtc = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
        row.Snapshot.LastUserActivityAtUtc = snapshot.LastUserActivityAt?.ToUnixTimeMilliseconds();
        var purpose = snapshot.Purpose ?? SessionPurpose.OngoingDefault;
        var policy = snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default;
        row.Snapshot.LifecycleStatus = SessionLifecycle.Align(snapshot.Status, snapshot.LifecycleStatus).ToString();
        row.Snapshot.PurposeKind = purpose.Kind.ToString();
        row.Snapshot.PurposeDescription = purpose.Description;
        row.Snapshot.DeadlineAtUtc = purpose.DeadlineAt?.ToUnixTimeMilliseconds();
        row.Snapshot.PurposeMetadataJson = SerializeMetadata(purpose.Metadata);
        row.Snapshot.AgentCompletion = policy.AgentCompletion.ToString();
        row.Snapshot.UserCompletionAllowed = policy.UserCompletionAllowed;
        row.Snapshot.UserCancellationAllowed = policy.UserCancellationAllowed;
        row.Snapshot.LifecycleReason = snapshot.LifecycleReason;
        row.Snapshot.LifecycleSource = snapshot.LifecycleSource?.ToString();
        row.Snapshot.LifecycleChangedAtUtc = snapshot.LifecycleChangedAt?.ToUnixTimeMilliseconds();
        row.Snapshot.SpeechLocaleOverride = snapshot.SpeechLocaleOverride;
        ApplyModelSelection(row.Snapshot, snapshot.ModelSelection);
    }

    private static SessionRecord ToRecord(SessionSnapshot snapshot)
    {
        var id = snapshot.SessionId.ToString("D");
        var row = new SessionRecord
        {
            SessionId = id,
            CreatedAtUtc = snapshot.CreatedAt.ToUnixTimeMilliseconds(),
            Entries = snapshot.Entries.Select(entry => ToRecord(id, entry)).ToList()
        };
        ApplySession(row, snapshot);
        return row;
    }

    private static EntryRecord ToRecord(string sessionId, ConversationEntry entry)
    {
        var row = new EntryRecord { EntryId = entry.EntryId.ToString("D"), SessionId = sessionId };
        ApplyEntry(row, entry);
        return row;
    }

    private static void ApplyEntry(EntryRecord row, ConversationEntry entry)
    {
        row.EntrySequence = entry.Sequence;
        row.SourceEventId = entry.SourceEventId?.ToString("D");
        row.Role = entry.Role.ToString();
        row.Text = entry.Text;
        row.ResponseId = entry.ResponseId?.ToString("D");
        row.Status = entry.Status.ToString();
        row.DeliveryMode = entry.DeliveryMode.ToString();
        row.HeardTextEndExclusive = entry.HeardTextEndExclusive;
        row.ReceivedTextEndExclusive = entry.ReceivedTextEndExclusive;
        row.EnvelopeJson = SerializeEnvelope(entry.Envelope);
        row.AttachmentRefsJson = SerializeAttachmentRefs(entry.Attachments);
        row.SourceAdmissionFingerprint = entry.SourceAdmissionFingerprint;
        row.FinishReason = entry.FinishReason;
        row.InterruptReason = entry.InterruptReason;
        ApplyModelProvenance(row, entry.ModelProvenance);
        row.CreatedAtUtc = entry.CreatedAt.ToUnixTimeMilliseconds();
    }

    private static SessionSnapshot ToSnapshot(SessionRecord row, IReadOnlyList<EntryRecord> entryRows)
    {
        var definition = JsonSerializer.Deserialize<AgentDefinition>(row.DefinitionJson, Json)
            ?? throw AgentCoreErrors.Persistence("Stored agent definition was empty.");
        var snapshot = row.Snapshot ?? new SnapshotRecord { SessionId = row.SessionId };
        var entries = entryRows.OrderBy(entry => entry.EntrySequence).Select(ToEntry).ToArray();
        return new SessionSnapshot(
            snapshot.SchemaVersion,
            Guid.Parse(row.SessionId),
            row.Revision,
            definition,
            Enum.Parse<SessionMode>(row.Mode),
            string.IsNullOrEmpty(row.PendingMode) ? null : Enum.Parse<SessionMode>(row.PendingMode),
            Enum.Parse<SessionStatus>(row.Status),
            entries,
            snapshot.Summary,
            snapshot.SummarizedThroughEntrySequence,
            snapshot.PendingTopic,
            string.IsNullOrEmpty(snapshot.ProfileId) ? null : Guid.Parse(snapshot.ProfileId),
            FromUnix(row.CreatedAtUtc),
            FromUnix(row.UpdatedAtUtc),
            snapshot.LastUserActivityAtUtc is { } lastUser ? FromUnix(lastUser) : null,
            row.PauseReason,
            string.IsNullOrEmpty(row.Title) ? SessionTitles.Default : row.Title,
            row.RuntimeEpoch,
            row.WorkspaceOwned,
            row.ArchivedAtUtc is { } archived ? FromUnix(archived) : null,
            row.DurablyDeletedAtUtc is { } deleted ? FromUnix(deleted) : null,
            snapshot.LastEntrySequence,
            ReadLifecycleStatus(row.Status, snapshot.LifecycleStatus),
            ReadPurpose(snapshot),
            ReadPolicy(snapshot),
            snapshot.LifecycleReason,
            ParseEnumOrNull<LifecycleTransitionSource>(snapshot.LifecycleSource),
            snapshot.LifecycleChangedAtUtc is { } changed ? FromUnix(changed) : null,
            snapshot.SpeechLocaleOverride,
            ReadModelSelection(snapshot),
            snapshot.SummaryFormatVersion,
            snapshot.SummaryGeneratedAtUtc is { } generatedAt ? FromUnix(generatedAt) : null,
            ReadSummaryModel(snapshot),
            string.IsNullOrEmpty(row.AgentInstanceId) ? null : Guid.Parse(row.AgentInstanceId),
            string.IsNullOrEmpty(row.PinnedPersonaJson)
                ? null
                : JsonSerializer.Deserialize<AgentIdentity>(row.PinnedPersonaJson, Json));
    }

    private static ConversationEntry ToEntry(EntryRecord row) =>
        new(
            Guid.Parse(row.EntryId),
            row.EntrySequence,
            string.IsNullOrEmpty(row.SourceEventId) ? null : Guid.Parse(row.SourceEventId),
            Enum.Parse<ConversationRole>(row.Role),
            row.Text,
            string.IsNullOrEmpty(row.ResponseId) ? null : Guid.Parse(row.ResponseId),
            Enum.Parse<EntryStatus>(row.Status),
            Enum.Parse<SessionMode>(row.DeliveryMode),
            row.HeardTextEndExclusive,
            row.ReceivedTextEndExclusive,
            FromUnix(row.CreatedAtUtc),
            DeserializeEnvelope(row.EnvelopeJson),
            DeserializeAttachmentRefs(row.AttachmentRefsJson),
            row.SourceAdmissionFingerprint,
            row.FinishReason,
            row.InterruptReason,
            ReadModelProvenance(row));

    private static string? SerializeAttachmentRefs(IReadOnlyList<ConversationAttachmentRef>? attachments) =>
        attachments is not { Count: > 0 }
            ? null
            : JsonSerializer.Serialize(attachments, Json);

    private static IReadOnlyList<ConversationAttachmentRef>? DeserializeAttachmentRefs(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<ConversationAttachmentRef[]>(json, Json);

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null || metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(metadata, Json);

    private static IReadOnlyDictionary<string, string>? DeserializeMetadata(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json);

    private static SessionLifecycleStatus ReadLifecycleStatus(string protocolStatus, string? stored)
    {
        var status = Enum.Parse<SessionStatus>(protocolStatus);
        var current = ParseEnumOrNull<SessionLifecycleStatus>(stored) ?? SessionLifecycle.FromProtocolStatus(status);
        return SessionLifecycle.Align(status, current);
    }

    private static SessionPurpose ReadPurpose(SnapshotRecord snapshot)
    {
        var kind = ParseEnumOrNull<SessionPurposeKind>(snapshot.PurposeKind) ?? SessionPurposeKind.Ongoing;
        return new SessionPurpose(
            kind,
            snapshot.PurposeDescription,
            snapshot.DeadlineAtUtc is { } deadline ? FromUnix(deadline) : null,
            DeserializeMetadata(snapshot.PurposeMetadataJson));
    }

    private static SessionCompletionPolicy ReadPolicy(SnapshotRecord snapshot) =>
        new(
            ParseEnumOrNull<AgentCompletionAuthority>(snapshot.AgentCompletion)
                ?? SessionCompletionPolicy.Default.AgentCompletion,
            snapshot.UserCompletionAllowed ?? true,
            snapshot.UserCancellationAllowed ?? true);

    private static void ApplyModelSelection(SnapshotRecord row, SessionModelSelection? selection)
    {
        row.ModelCatalogKey = selection?.CatalogKey;
        row.ModelProviderAlias = selection?.ProviderAlias;
        row.ModelId = selection?.ModelId;
        row.ModelSelectionSource = selection?.SelectionSource.ToString();
        row.ModelReasoningEffort = selection?.ReasoningEffort;
    }

    private static SessionModelSelection? ReadModelSelection(SnapshotRecord row)
    {
        if (string.IsNullOrEmpty(row.ModelCatalogKey)
            || string.IsNullOrEmpty(row.ModelProviderAlias)
            || string.IsNullOrEmpty(row.ModelId)
            || string.IsNullOrEmpty(row.ModelSelectionSource))
        {
            return null;
        }

        return new SessionModelSelection(
            row.ModelCatalogKey,
            row.ModelProviderAlias,
            row.ModelId,
            Enum.Parse<ModelSelectionSource>(row.ModelSelectionSource),
            row.ModelReasoningEffort);
    }

    private static ModelGenerationProvenance? ReadSummaryModel(SnapshotRecord row)
    {
        if (string.IsNullOrEmpty(row.SummaryModelCatalogKey)
            || string.IsNullOrEmpty(row.SummaryModelProviderAlias)
            || string.IsNullOrEmpty(row.SummaryModelId))
        {
            return null;
        }

        return new ModelGenerationProvenance(
            row.SummaryModelCatalogKey,
            row.SummaryModelProviderAlias,
            row.SummaryModelId,
            row.SummaryModelReasoningEffort);
    }

    private static void ApplyModelProvenance(EntryRecord row, ModelGenerationProvenance? provenance)
    {
        row.ModelCatalogKey = provenance?.CatalogKey;
        row.ModelProviderAlias = provenance?.ProviderAlias;
        row.ModelId = provenance?.ModelId;
        row.ModelReasoningEffort = provenance?.ReasoningEffort;
    }

    private static ModelGenerationProvenance? ReadModelProvenance(EntryRecord row)
    {
        if (string.IsNullOrEmpty(row.ModelCatalogKey)
            || string.IsNullOrEmpty(row.ModelProviderAlias)
            || string.IsNullOrEmpty(row.ModelId))
        {
            return null;
        }

        return new ModelGenerationProvenance(
            row.ModelCatalogKey,
            row.ModelProviderAlias,
            row.ModelId,
            row.ModelReasoningEffort);
    }

    private static TEnum? ParseEnumOrNull<TEnum>(string? value) where TEnum : struct, Enum =>
        string.IsNullOrEmpty(value) ? null : Enum.Parse<TEnum>(value);

    private static bool EnvelopeUnchanged(string? json, ResponseEnvelope? envelope)
    {
        var stored = ResponseEnvelopeJson.Deserialize(json);
        if (stored is null)
        {
            return envelope is null;
        }

        if (envelope is null)
        {
            return false;
        }

        if (stored.DisplayText != envelope.DisplayText
            || stored.SpeechMode != envelope.SpeechMode
            || stored.SpeechText != envelope.SpeechText
            || stored.Blocks.Count != envelope.Blocks.Count)
        {
            return false;
        }

        for (var index = 0; index < stored.Blocks.Count; index++)
        {
            if (stored.Blocks[index] != envelope.Blocks[index])
            {
                return false;
            }
        }

        return true;
    }

    private static string? SerializeEnvelope(ResponseEnvelope? envelope) =>
        envelope is null ? null : ResponseEnvelopeJson.Serialize(envelope);

    private static ResponseEnvelope? DeserializeEnvelope(string? json) =>
        ResponseEnvelopeJson.Deserialize(json);

    private static DateTimeOffset FromUnix(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    private static async Task<bool> HasCompleteLegacySchemaAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, ColumnSpec[]>(StringComparer.Ordinal)
        {
            ["Sessions"] =
            [
                new("SessionId", "TEXT", NotNull: true, Pk: true),
                new("AgentId", "TEXT", NotNull: true, Pk: false),
                new("AgentVersion", "INTEGER", NotNull: true, Pk: false),
                new("DefinitionJson", "TEXT", NotNull: true, Pk: false),
                new("Mode", "TEXT", NotNull: true, Pk: false),
                new("PendingMode", "TEXT", NotNull: false, Pk: false),
                new("Status", "TEXT", NotNull: true, Pk: false),
                new("CreatedAtUtc", "INTEGER", NotNull: true, Pk: false),
                new("UpdatedAtUtc", "INTEGER", NotNull: true, Pk: false),
                new("Revision", "INTEGER", NotNull: true, Pk: false)
            ],
            ["UserProfiles"] =
            [
                new("ProfileId", "TEXT", NotNull: true, Pk: true),
                new("PreferencesJson", "TEXT", NotNull: true, Pk: false),
                new("Revision", "INTEGER", NotNull: true, Pk: false),
                new("UpdatedAtUtc", "INTEGER", NotNull: true, Pk: false)
            ],
            ["ConversationEntries"] =
            [
                new("EntryId", "TEXT", NotNull: true, Pk: true),
                new("SessionId", "TEXT", NotNull: true, Pk: false),
                new("EntrySequence", "INTEGER", NotNull: true, Pk: false),
                new("SourceEventId", "TEXT", NotNull: false, Pk: false),
                new("Role", "TEXT", NotNull: true, Pk: false),
                new("Text", "TEXT", NotNull: true, Pk: false),
                new("ResponseId", "TEXT", NotNull: false, Pk: false),
                new("Status", "TEXT", NotNull: true, Pk: false),
                new("DeliveryMode", "TEXT", NotNull: true, Pk: false),
                new("HeardTextEndExclusive", "INTEGER", NotNull: true, Pk: false),
                new("ReceivedTextEndExclusive", "INTEGER", NotNull: true, Pk: false),
                new("CreatedAtUtc", "INTEGER", NotNull: true, Pk: false)
            ],
            ["SessionSnapshots"] =
            [
                new("SessionId", "TEXT", NotNull: true, Pk: true),
                new("SchemaVersion", "INTEGER", NotNull: true, Pk: false),
                new("Summary", "TEXT", NotNull: true, Pk: false),
                new("SummarizedThroughEntrySequence", "INTEGER", NotNull: true, Pk: false),
                new("PendingTopic", "TEXT", NotNull: false, Pk: false),
                new("ProfileId", "TEXT", NotNull: false, Pk: false),
                new("LastEntrySequence", "INTEGER", NotNull: true, Pk: false),
                new("UpdatedAtUtc", "INTEGER", NotNull: true, Pk: false)
            ]
        };

        foreach (var (table, columns) in tables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (!await ColumnsMatchAsync(connection, table, columns, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        if (!await HasForeignKeyAsync(connection, "SessionSnapshots", "SessionId", "Sessions", "SessionId", "CASCADE", cancellationToken).ConfigureAwait(false)
            || !await HasForeignKeyAsync(connection, "ConversationEntries", "SessionId", "Sessions", "SessionId", "CASCADE", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await HasUniqueIndexAsync(
                connection,
                "ConversationEntries",
                ["SessionId", "EntrySequence"],
                partial: false,
                partialPredicate: null,
                cancellationToken)
            .ConfigureAwait(false)
            && await HasUniqueIndexAsync(
                connection,
                "ConversationEntries",
                ["SessionId", "SourceEventId"],
                partial: true,
                partialPredicate: "SourceEventId IS NOT NULL",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HasEnsureCreatedCurrentSchemaAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken) =>
        await TableExistsAsync(connection, "OwnerCapabilities", cancellationToken).ConfigureAwait(false)
        && await ColumnExistsAsync(connection, "Sessions", "Title", cancellationToken).ConfigureAwait(false);

    private static async Task<bool> ColumnExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct ColumnSpec(string Name, string Type, bool NotNull, bool Pk);

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        var name = command.CreateParameter();
        name.ParameterName = "$name";
        name.Value = table;
        command.Parameters.Add(name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) != 0;
    }

    private static async Task<bool> ColumnsMatchAsync(
        System.Data.Common.DbConnection connection,
        string table,
        ColumnSpec[] expected,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info(\"{table}\");";
        var found = new Dictionary<string, ColumnSpec>(StringComparer.Ordinal);
        await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            var notNull = reader.GetInt64(3) != 0;
            var pk = reader.GetInt64(5) > 0;
            found[name] = new ColumnSpec(name, type, notNull, pk);
        }

        foreach (var column in expected)
        {
            if (!found.TryGetValue(column.Name, out var actual)
                || !string.Equals(actual.Type, column.Type, StringComparison.OrdinalIgnoreCase)
                || actual.NotNull != column.NotNull
                || actual.Pk != column.Pk)
            {
                return false;
            }
        }

        return found.Count == expected.Length;
    }

    private static async Task<bool> HasForeignKeyAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string from,
        string toTable,
        string to,
        string onDelete,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(2), toTable, StringComparison.Ordinal)
                && string.Equals(reader.GetString(3), from, StringComparison.Ordinal)
                && string.Equals(reader.GetString(4), to, StringComparison.Ordinal)
                && string.Equals(reader.GetString(6), onDelete, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasUniqueIndexAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string[] columns,
        bool partial,
        string? partialPredicate,
        CancellationToken cancellationToken)
    {
        await using var list = connection.CreateCommand();
        list.CommandText = $"PRAGMA index_list(\"{table}\");";
        var indexes = new List<(string Name, bool Unique, bool Partial)>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                indexes.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(4) != 0));
            }
        }

        foreach (var index in indexes.Where(item => item.Unique && item.Partial == partial))
        {
            if (partialPredicate is not null
                && !await IndexPredicateMatchesAsync(connection, index.Name, partialPredicate, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info(\"{index.Name}\");";
            var actual = new List<string>();
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add(reader.GetString(2));
            }

            if (actual.Count == columns.Length && actual.Zip(columns).All(pair => pair.First == pair.Second))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IndexPredicateMatchesAsync(
        System.Data.Common.DbConnection connection,
        string indexName,
        string expectedPredicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        var name = command.CreateParameter();
        name.ParameterName = "$name";
        name.Value = indexName;
        command.Parameters.Add(name);
        var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var whereIndex = sql.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
        if (whereIndex < 0)
        {
            return false;
        }

        var predicate = sql[(whereIndex + " WHERE ".Length)..].Trim();
        return string.Equals(NormalizeSql(predicate), NormalizeSql(expectedPredicate), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task SaveChangesOrThrowAsync(AgentCoreDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueKeyViolation(ex))
        {
            throw AgentCoreErrors.Conflict("Session revision conflict.");
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Persistence("Persistent save failed.");
        }
    }

    private static bool IsUniqueKeyViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqliteException sqlite && sqlite.SqliteExtendedErrorCode is 1555 or 2067)
            {
                return true;
            }
        }

        return false;
    }
}
