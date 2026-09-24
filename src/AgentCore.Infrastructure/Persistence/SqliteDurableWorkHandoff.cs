using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Domain.Work;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteDurableWorkHandoff : IDurableWorkHandoff
{
    private readonly IDbContextFactory<AgentCoreDbContext> contexts;
    private readonly Action? beforeCommit;

    internal SqliteDurableWorkHandoff(IDbContextFactory<AgentCoreDbContext> contexts, Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        this.contexts = contexts;
        this.beforeCommit = beforeCommit;
    }

    public async ValueTask<WorkItemCreateResult> AcceptAsync(
        Guid occurrenceId,
        WorkItem proposed,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        try
        {
            return await AcceptCoreAsync(occurrenceId, proposed, acceptedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception))
        {
            return await AcceptCoreAsync(occurrenceId, proposed, acceptedAtUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WorkItemCreateResult> AcceptCoreAsync(
        Guid occurrenceId,
        WorkItem proposed,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var occurrenceKey = occurrenceId.ToString("D");
        var row = await db.TriggerOccurrences
            .FirstOrDefaultAsync(item => item.OccurrenceId == occurrenceKey, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw AgentCoreErrors.NotFound("Occurrence was not found.");
        }

        var occurrence = TriggerStoreMapping.ToOccurrence(row);
        var sourceKey = occurrence.OccurrenceId.ToString("D");
        var workRow = await db.WorkItems
            .FirstOrDefaultAsync(item => item.SourceOccurrenceId == sourceKey, cancellationToken)
            .ConfigureAwait(false);
        WorkItem? existing = null;
        if (workRow is not null)
        {
            var approval = await SqliteWorkItemStore.LoadApprovalAsync(db, workRow, cancellationToken)
                .ConfigureAwait(false);
            existing = WorkStoreMapping.ToWorkItem(workRow, approval);
        }
        else
        {
            var proposedKey = proposed.WorkItemId.ToString("D");
            var idTaken = await db.WorkItems
                .AnyAsync(item => item.WorkItemId == proposedKey, cancellationToken)
                .ConfigureAwait(false);
            if (idTaken)
            {
                throw AgentCoreErrors.Conflict("Work item already exists.");
            }
        }

        var decision = DurableWorkAcceptance.Accept(occurrence, existing, proposed, acceptedAtUtc);
        if (!decision.Changed)
        {
            return decision.Result;
        }

        TriggerStoreMapping.CopyRouting(row, decision.Occurrence);
        if (decision.Result.Kind == WorkItemCreateKind.Created)
        {
            db.WorkItems.Add(WorkStoreMapping.ToRecord(decision.Result.Item));
        }

        beforeCommit?.Invoke();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return decision.Result;
    }

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19;
}
