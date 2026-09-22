using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteStructuredMemoryStore(IDbContextFactory<AgentCoreDbContext> contexts) : IStructuredMemoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<StructuredMemoryItem?> FindAsync(
        Guid sessionId,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.StructuredMemories.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.MemoryId == memoryId.ToString("D") && item.SessionId == sessionId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async ValueTask<StructuredMemoryItem?> FindActiveBySubjectAsync(
        Guid sessionId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.StructuredMemories.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.SessionId == sessionId.ToString("D")
                    && item.Status == (int)MemoryItemStatus.Active
                    && item.Kind == (int)kind
                    && item.SubjectKey == subjectKey,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async ValueTask<int> CountActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.StructuredMemories.CountAsync(
            item => item.SessionId == sessionId.ToString("D") && item.Status == (int)MemoryItemStatus.Active,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.StructuredMemories.AsNoTracking()
            .Where(item => item.SessionId == sessionId.ToString("D") && item.Status == (int)MemoryItemStatus.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async ValueTask InsertAsync(StructuredMemoryItem item, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = item.SessionId.ToString("D");
        var active = await db.StructuredMemories.CountAsync(
            row => row.SessionId == sessionId && row.Status == (int)MemoryItemStatus.Active,
            cancellationToken).ConfigureAwait(false);
        if (active >= MemoryLimits.MaxActiveItems)
        {
            throw new AgentCoreException("MemoryCapacity", "Active session memory is full.", 409);
        }

        db.StructuredMemories.Add(Map(item));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new AgentCoreException(
                "Conflict",
                "An active memory already uses this subject. Update that item.",
                409);
        }
    }

    public async ValueTask SupersedeAsync(
        StructuredMemoryItem superseded,
        StructuredMemoryItem created,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await db.StructuredMemories
            .FirstOrDefaultAsync(row => row.MemoryId == superseded.MemoryId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || current.SessionId != superseded.SessionId.ToString("D")
            || current.Status != (int)MemoryItemStatus.Active)
        {
            throw AgentCoreErrors.NotFound("Memory was not found.");
        }

        Copy(current, superseded);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.StructuredMemories.Add(Map(created));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new AgentCoreException(
                "Conflict",
                "An active memory already uses this subject. Update that item.",
                409);
        }
    }

    public async ValueTask TombstoneAsync(StructuredMemoryItem tombstone, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var current = await db.StructuredMemories
            .FirstOrDefaultAsync(row => row.MemoryId == tombstone.MemoryId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || current.SessionId != tombstone.SessionId.ToString("D")
            || current.Status != (int)MemoryItemStatus.Active)
        {
            throw AgentCoreErrors.NotFound("Memory was not found.");
        }

        Copy(current, tombstone);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sessionId.ToString("D");
        await db.StructuredMemories.Where(row => row.SessionId == key)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static StructuredMemoryItem Map(StructuredMemoryRecord row)
    {
        var entries = JsonSerializer.Deserialize<List<Guid>>(row.SourceEntryIdsJson, Json) ?? [];
        return new StructuredMemoryItem(
            Guid.Parse(row.MemoryId),
            Guid.Parse(row.SessionId),
            (MemoryKind)row.Kind,
            (MemoryItemStatus)row.Status,
            row.Subject,
            row.Content,
            row.SubjectKey,
            new MemoryProvenance(
                row.Source,
                entries,
                row.SupersedesMemoryId is null ? null : Guid.Parse(row.SupersedesMemoryId),
                DateTimeOffset.FromUnixTimeMilliseconds(row.ProvenanceRecordedAtUtc)),
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));
    }

    private static StructuredMemoryRecord Map(StructuredMemoryItem item) =>
        new()
        {
            MemoryId = item.MemoryId.ToString("D"),
            SessionId = item.SessionId.ToString("D"),
            Kind = (int)item.Kind,
            Status = (int)item.Status,
            Subject = item.Subject,
            Content = item.Content,
            SubjectKey = item.SubjectKey,
            Source = item.Provenance.Source,
            SourceEntryIdsJson = JsonSerializer.Serialize(item.Provenance.SourceEntryIds, Json),
            SupersedesMemoryId = item.Provenance.SupersedesMemoryId?.ToString("D"),
            ProvenanceRecordedAtUtc = item.Provenance.RecordedAt.ToUnixTimeMilliseconds(),
            CreatedAtUtc = item.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = item.UpdatedAt.ToUnixTimeMilliseconds()
        };

    private static void Copy(StructuredMemoryRecord row, StructuredMemoryItem item)
    {
        var mapped = Map(item);
        row.Kind = mapped.Kind;
        row.Status = mapped.Status;
        row.Subject = mapped.Subject;
        row.Content = mapped.Content;
        row.SubjectKey = mapped.SubjectKey;
        row.Source = mapped.Source;
        row.SourceEntryIdsJson = mapped.SourceEntryIdsJson;
        row.SupersedesMemoryId = mapped.SupersedesMemoryId;
        row.ProvenanceRecordedAtUtc = mapped.ProvenanceRecordedAtUtc;
        row.CreatedAtUtc = mapped.CreatedAtUtc;
        row.UpdatedAtUtc = mapped.UpdatedAtUtc;
    }
}
