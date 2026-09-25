using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAdminEventStore(IDbContextFactory<AgentCoreDbContext> contexts, IIdGenerator ids)
    : IAdminEventStore
{
    public async ValueTask<AdminEvent> AppendAsync(AdminEventAppend append, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await AdminEventPersistence.TryGetByOperationIdAsync(db, append.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var eventId = ids.NewId();
        AdminEventPersistence.StageAppend(db, append, eventId);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var raced = await AdminEventPersistence.TryGetByOperationIdAsync(db, append.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }

        return AdminEventPersistence.Map(
            await db.AdminEvents.AsNoTracking()
                .SingleAsync(item => item.EventId == eventId.ToString("D"), cancellationToken)
                .ConfigureAwait(false));
    }

    public async ValueTask<IReadOnlyList<AdminEvent>> ListAsync(
        AdminEventListQuery query,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = db.AdminEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            rows = rows.Where(item => item.TargetType == query.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            rows = rows.Where(item => item.TargetId == query.TargetId);
        }

        var ordered = await rows
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.EventId)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ordered.Select(AdminEventPersistence.Map).ToArray();
    }
}
