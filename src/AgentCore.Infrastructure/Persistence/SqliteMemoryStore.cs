using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
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
        source.BackupDatabase(destination);
    }

    public async ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var row = await db.Sessions
            .AsNoTracking()
            .Include(item => item.Snapshot)
            .Include(item => item.Entries)
            .SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSnapshot(row);
    }

    public async ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = snapshot.SessionId.ToString("D");
        var existing = await db.Sessions
            .Include(item => item.Snapshot)
            .Include(item => item.Entries)
            .SingleOrDefaultAsync(item => item.SessionId == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            if (expectedRevision != 0 || snapshot.Revision != 1)
            {
                throw AgentCoreErrors.Conflict("Insert requires expectedRevision 0 and snapshot.Revision 1.");
            }

            db.Sessions.Add(ToRecord(snapshot));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = ToSnapshot(existing);
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
        UpsertEntries(db, existing, snapshot);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        var rows = await db.Entries.AsNoTracking()
            .Where(entry => entry.SessionId == key && entry.EntrySequence > afterEntrySequence)
            .OrderBy(entry => entry.EntrySequence)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToEntry).ToArray();
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

        var preferences = JsonSerializer.Deserialize<Dictionary<string, string>>(row.PreferencesJson, Json)
            ?? [];
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
                PreferencesJson = JsonSerializer.Serialize(profile.Preferences, Json),
                Revision = profile.Revision,
                UpdatedAtUtc = profile.UpdatedAt.ToUnixTimeMilliseconds()
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                throw AgentCoreErrors.Conflict("Profile already exists.");
            }

            return;
        }

        if (existing.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Stale profile revision.");
        }

        existing.PreferencesJson = JsonSerializer.Serialize(profile.Preferences, Json);
        existing.Revision = profile.Revision;
        existing.UpdatedAtUtc = profile.UpdatedAt.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Sessions.Include(item => item.Snapshot).Include(item => item.Entries)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            var recovered = MemoryStoreSemantics.Recover(ToSnapshot(row), now);
            if (recovered.Revision == row.Revision)
            {
                continue;
            }

            ApplySession(row, recovered);
            UpsertEntries(db, row, recovered);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void UpsertEntries(AgentCoreDbContext db, SessionRecord session, SessionSnapshot snapshot)
    {
        var existing = session.Entries.ToDictionary(entry => entry.EntryId, StringComparer.Ordinal);
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
        row.Mode = snapshot.Mode.ToString();
        row.PendingMode = snapshot.PendingMode?.ToString();
        row.Status = snapshot.Status.ToString();
        row.UpdatedAtUtc = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
        row.Revision = snapshot.Revision;
        row.Snapshot ??= new SnapshotRecord { SessionId = row.SessionId };
        row.Snapshot.SchemaVersion = snapshot.SchemaVersion;
        row.Snapshot.Summary = snapshot.Summary;
        row.Snapshot.SummarizedThroughEntrySequence = snapshot.SummarizedThroughEntrySequence;
        row.Snapshot.PendingTopic = snapshot.PendingTopic;
        row.Snapshot.ProfileId = snapshot.ProfileId?.ToString("D");
        row.Snapshot.LastEntrySequence = snapshot.Entries.Count == 0 ? 0 : snapshot.Entries[^1].Sequence;
        row.Snapshot.UpdatedAtUtc = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
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
        row.CreatedAtUtc = entry.CreatedAt.ToUnixTimeMilliseconds();
    }

    private static SessionSnapshot ToSnapshot(SessionRecord row)
    {
        var definition = JsonSerializer.Deserialize<AgentDefinition>(row.DefinitionJson, Json)
            ?? throw AgentCoreErrors.Persistence("Stored agent definition was empty.");
        var snapshot = row.Snapshot ?? new SnapshotRecord { SessionId = row.SessionId };
        var entries = row.Entries.OrderBy(entry => entry.EntrySequence).Select(ToEntry).ToArray();
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
            FromUnix(row.UpdatedAtUtc));
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
            FromUnix(row.CreatedAtUtc));

    private static DateTimeOffset FromUnix(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}
