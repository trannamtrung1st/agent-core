using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Work;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteConversationTurnExecutionStore(IDbContextFactory<AgentCoreDbContext> contexts)
    : IConversationTurnExecutionStore
{
    public async ValueTask<ConversationTurnExecutionCreateResult> CreateAsync(
        ConversationTurnExecution item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsInitialQueued)
        {
            throw AgentCoreErrors.Validation("Only a new queued turn execution can be created.");
        }

        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.ConversationTurnExecutions.Add(ConversationTurnExecutionStoreMapping.ToRecord(item));
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new ConversationTurnExecutionCreateResult(ConversationTurnExecutionCreateKind.Created, item);
            }
            catch (DbUpdateException exception) when (IsConstraint(exception))
            {
            }
        }

        var existing = await GetBySourceEventAsync(item.SessionId, item.SourceEventId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            throw AgentCoreErrors.Conflict("Turn execution already exists.");
        }

        return new ConversationTurnExecutionCreateResult(ConversationTurnExecutionCreateKind.Existing, existing);
    }

    public async ValueTask<ConversationTurnExecution?> GetAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ConversationTurnExecutions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ExecutionId == Id(executionId), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ConversationTurnExecutionStoreMapping.ToDomain(row);
    }

    public async ValueTask<ConversationTurnExecution?> GetBySourceEventAsync(
        Guid sessionId,
        Guid sourceEventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ConversationTurnExecutions.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.SessionId == Id(sessionId) && item.SourceEventId == Id(sourceEventId),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ConversationTurnExecutionStoreMapping.ToDomain(row);
    }

    public async ValueTask<IReadOnlyList<ConversationTurnExecution>> ListOpenForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ConversationTurnExecutions.AsNoTracking()
            .Where(item => item.SessionId == Id(sessionId)
                && (item.Status == (int)ConversationTurnExecutionStatus.Queued
                    || item.Status == (int)ConversationTurnExecutionStatus.Running
                    || item.Status == (int)ConversationTurnExecutionStatus.WaitingForApproval))
            .OrderBy(item => item.AcceptedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ConversationTurnExecutionStoreMapping.ToDomain).ToArray();
    }

    public async ValueTask<IReadOnlyList<ConversationTurnExecution>> ListRunnableAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 100);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ConversationTurnExecutions.AsNoTracking()
            .Where(item => item.Status == (int)ConversationTurnExecutionStatus.Queued)
            .OrderBy(item => item.AcceptedAtUtc)
            .ThenBy(item => item.ExecutionId)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ConversationTurnExecutionStoreMapping.ToDomain).ToArray();
    }

    public ValueTask<ConversationTurnExecution> AdmitActiveSkillsAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        IReadOnlyList<string> skillIds,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item => item.AdmitActiveSkills(expectedRevision, generation, skillIds, updatedAtUtc),
            cancellationToken);

    public ValueTask<ConversationTurnExecution> AdmitCapabilitiesAsync(Guid executionId, long expectedRevision, Guid generation,
        IReadOnlyList<string> names, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        MutateAsync(executionId, item => item.AdmitCapabilities(expectedRevision, generation, names, updatedAtUtc), cancellationToken);

    public async ValueTask<ConversationTurnExecution?> TryClaimAsync(
        Guid executionId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ConversationTurnExecutions
            .FirstOrDefaultAsync(item => item.ExecutionId == Id(executionId), cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var current = ConversationTurnExecutionStoreMapping.ToDomain(row);
        try
        {
            var updated = current.TakeClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
            ConversationTurnExecutionStoreMapping.Apply(row, updated);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (WorkItemTransitionException exception) when (exception.Failure == WorkTransitionFailure.NotClaimable)
        {
            return null;
        }
        catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
        {
            throw ConversationTurnExecutionStoreMapping.Map(exception);
        }
    }

    public ValueTask<ConversationTurnExecution> RenewClaimAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item => item.RenewClaim(expectedRevision, generation, leaseExpiresAtUtc, renewedAtUtc),
            cancellationToken);

    public ValueTask<ConversationTurnExecution> AttachAssistantAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        Guid assistantEntryId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithAssistant(assistantEntryId, updatedAtUtc);
            },
            cancellationToken);

    public ValueTask<ConversationTurnExecution> CompleteAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item => item.CompleteTerminal(expectedRevision, generation, completedAtUtc),
            cancellationToken);

    public ValueTask<ConversationTurnExecution> FailAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item => item.FailTerminal(expectedRevision, generation, failedAtUtc),
            cancellationToken);

    public ValueTask<ConversationTurnExecution> CommitCancellationAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item => item.CommitCancellation(expectedRevision, generation, cancelledAtUtc),
            cancellationToken);

    public ValueTask<ConversationTurnExecution> MarkWaitingForApprovalAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithStatus(ConversationTurnExecutionStatus.WaitingForApproval, updatedAtUtc);
            },
            cancellationToken);

    public ValueTask<ConversationTurnExecution> ResumeRunningAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithStatus(ConversationTurnExecutionStatus.Running, updatedAtUtc);
            },
            cancellationToken);

    public async ValueTask<ConversationTurnExecution> RequestCancellationAsync(
        Guid sessionId,
        Guid executionId,
        long expectedRevision,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
            executionId,
            item =>
            {
                if (item.SessionId != sessionId)
                {
                    throw AgentCoreErrors.NotFound("Turn execution was not found.");
                }

                if (item.Revision != expectedRevision)
                {
                    throw new WorkItemTransitionException(WorkTransitionFailure.StaleRevision, "Turn execution revision is stale.");
                }

                return item.WithCancellationRequested(requestedAtUtc);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ConversationTurnExecutions
            .Where(item => item.Status == (int)ConversationTurnExecutionStatus.Running
                && item.ClaimLeaseExpiresAtUtc != null
                && item.ClaimLeaseExpiresAtUtc <= asOfUtc.ToUnixTimeMilliseconds())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var count = 0;
        foreach (var row in rows)
        {
            var current = ConversationTurnExecutionStoreMapping.ToDomain(row);
            var updated = current.RequeueExpiredClaim(asOfUtc);
            ConversationTurnExecutionStoreMapping.Apply(row, updated);
            count++;
        }

        if (count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    private async ValueTask<ConversationTurnExecution> MutateAsync(
        Guid executionId,
        Func<ConversationTurnExecution, ConversationTurnExecution> mutate,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ConversationTurnExecutions
            .FirstOrDefaultAsync(item => item.ExecutionId == Id(executionId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Turn execution was not found.");
        var current = ConversationTurnExecutionStoreMapping.ToDomain(row);
        try
        {
            var updated = mutate(current);
            ConversationTurnExecutionStoreMapping.Apply(row, updated);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
        {
            throw ConversationTurnExecutionStoreMapping.Map(exception);
        }
    }

    private static string Id(Guid value) => value.ToString("D");

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && (sqlite.SqliteErrorCode == 19 || sqlite.SqliteExtendedErrorCode == 2067);
}
