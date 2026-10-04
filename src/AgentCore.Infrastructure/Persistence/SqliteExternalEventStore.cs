using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteExternalEventStore(IDbContextFactory<AgentCoreDbContext> contexts) : IExternalEventStore
{
    public async ValueTask<ExternalEventSource> CreateAsync(
        ExternalEventSource source,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ExternalEventSources.Add(ToRecord(source));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Conflict("Event source key is already in use.");
        }

        return source;
    }

    public async ValueTask<ExternalEventSource?> GetAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExternalEventSources.AsNoTracking()
            .FirstOrDefaultAsync(item => item.SourceId == sourceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSource(row);
    }

    public async ValueTask<ExternalEventSource?> GetByKeyAsync(Guid sourceKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = sourceKey.ToString("D");
        var row = await db.ExternalEventSources.AsNoTracking()
            .FirstOrDefaultAsync(item => item.SourceKey == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSource(row);
    }

    public async ValueTask<IReadOnlyList<ExternalEventSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExternalEventSources.AsNoTracking()
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.SourceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToSource).ToArray();
    }

    public async ValueTask<ExternalEventSource> SaveAsync(
        ExternalEventSource source,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = source.SourceId.ToString("D");
        var row = await db.ExternalEventSources
            .FirstOrDefaultAsync(item => item.SourceId == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Event source revision is stale.");
        }

        Copy(row, source);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return source;
    }

    public async ValueTask<ExternalEventAdmit> AdmitAsync(ExternalEvent candidate, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = candidate.SourceId.ToString("D");
        var existing = await db.ExternalEvents.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.SourceId == sourceId && item.SourceEventId == candidate.SourceEventId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, ToEvent(existing));
        }

        db.ExternalEvents.Add(ToRecord(candidate));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ExternalEventAdmit(ExternalEventAdmitKind.Admitted, candidate);
        }
        catch (DbUpdateException)
        {
            var raced = await db.ExternalEvents.AsNoTracking()
                .FirstAsync(
                    item => item.SourceId == sourceId && item.SourceEventId == candidate.SourceEventId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, ToEvent(raced));
        }
    }

    public async ValueTask<ExternalEvent?> GetEventAsync(
        Guid sourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExternalEvents.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.SourceId == sourceId.ToString("D") && item.SourceEventId == sourceEventId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToEvent(row);
    }

    private static ExternalEventSourceRecord ToRecord(ExternalEventSource source) => new()
    {
        SourceId = source.SourceId.ToString("D"),
        DisplayName = source.DisplayName,
        Kind = (int)source.Kind,
        SourceKey = source.SourceKey.ToString("D"),
        CredentialHash = source.CredentialHash,
        Status = (int)source.Status,
        Revision = source.Revision,
        CreatedAtUtc = source.CreatedAtUtc.ToUnixTimeMilliseconds(),
        UpdatedAtUtc = source.UpdatedAtUtc.ToUnixTimeMilliseconds()
    };

    private static void Copy(ExternalEventSourceRecord row, ExternalEventSource source)
    {
        row.DisplayName = source.DisplayName;
        row.CredentialHash = source.CredentialHash;
        row.Status = (int)source.Status;
        row.Revision = source.Revision;
        row.UpdatedAtUtc = source.UpdatedAtUtc.ToUnixTimeMilliseconds();
    }

    private static ExternalEventSource ToSource(ExternalEventSourceRecord row) => new(
        Guid.Parse(row.SourceId),
        row.DisplayName,
        (ExternalEventSourceKind)row.Kind,
        Guid.Parse(row.SourceKey),
        row.CredentialHash,
        (ExternalEventSourceStatus)row.Status,
        row.Revision,
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));

    private static ExternalEventRecord ToRecord(ExternalEvent item) => new()
    {
        EventId = item.EventId.ToString("D"),
        SourceId = item.SourceId.ToString("D"),
        SourceEventId = item.SourceEventId,
        EventType = item.EventType,
        OccurredAtUtc = item.OccurredAtUtc.ToUnixTimeMilliseconds(),
        AdmittedAtUtc = item.AdmittedAtUtc.ToUnixTimeMilliseconds(),
        EvidenceJson = item.EvidenceJson
    };

    private static ExternalEvent ToEvent(ExternalEventRecord row) => new(
        Guid.Parse(row.EventId),
        Guid.Parse(row.SourceId),
        row.SourceEventId,
        row.EventType,
        DateTimeOffset.FromUnixTimeMilliseconds(row.OccurredAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAtUtc),
        row.EvidenceJson);
}
