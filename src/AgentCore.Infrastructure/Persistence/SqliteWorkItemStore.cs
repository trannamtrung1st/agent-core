using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Work;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteWorkItemStore(IDbContextFactory<AgentCoreDbContext> contexts) : IWorkItemStore
{
    public async ValueTask<WorkItemCreateResult> CreateAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsInitialQueued)
        {
            throw AgentCoreErrors.Validation("Only a new queued work item can be created.");
        }

        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.WorkItems.Add(WorkStoreMapping.ToRecord(item));
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new WorkItemCreateResult(WorkItemCreateKind.Created, item);
            }
            catch (DbUpdateException exception) when (IsConstraint(exception))
            {
            }
        }

        var existing = await GetBySourceOccurrenceAsync(item.Provenance.SourceOccurrenceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            throw AgentCoreErrors.Conflict("Work item already exists.");
        }

        if (!existing.Owner.Equals(item.Owner))
        {
            throw AgentCoreErrors.Conflict("Source occurrence is already owned.");
        }

        return new WorkItemCreateResult(WorkItemCreateKind.Existing, existing);
    }

    public async ValueTask<WorkItem?> GetAsync(WorkOwner owner, Guid workItemId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindRowAsync(db, workItemId, cancellationToken).ConfigureAwait(false);
        if (row is null || !OwnedBy(row, owner))
        {
            return null;
        }

        var approval = await LoadApprovalAsync(db, row, cancellationToken).ConfigureAwait(false);
        return WorkStoreMapping.ToWorkItem(row, approval);
    }

    public async ValueTask<WorkItem?> GetBySourceOccurrenceAsync(
        Guid sourceOccurrenceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = sourceOccurrenceId.ToString("D");
        var row = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.SourceOccurrenceId == sourceId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var approval = await LoadApprovalAsync(db, row, cancellationToken).ConfigureAwait(false);
        return WorkStoreMapping.ToWorkItem(row, approval);
    }

    public async ValueTask<IReadOnlyList<WorkItem>> ListAsync(
        WorkOwner owner,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var rows = await db.WorkItems.AsNoTracking()
            .Where(item => item.AgentInstanceId == instanceId && item.ProfileId == profileId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.WorkItemId)
            .Take(Clamp(limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await MapAllAsync(db, rows, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkItem>> ListRunnableAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var asOf = asOfUtc.ToUnixTimeMilliseconds();
        var queued = (int)WorkItemStatus.Queued;
        var retry = (int)WorkItemStatus.WaitingToRetry;
        var rows = await db.WorkItems.AsNoTracking()
            .Where(item => item.Status == queued || (item.Status == retry && item.NextRetryAtUtc <= asOf))
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.WorkItemId)
            .Take(Clamp(limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await MapAllAsync(db, rows, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<WorkItem?> TryClaimAsync(
        Guid workItemId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            workItemId,
            item => item.TakeClaim(generation, claimedAtUtc, leaseExpiresAtUtc),
            cancellationToken,
            notClaimable: true);

    public ValueTask<WorkItem> RenewClaimAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.RenewClaim(expectedRevision, generation, leaseExpiresAtUtc, renewedAtUtc),
            cancellationToken));

    public ValueTask<WorkItem> CheckpointAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkCheckpoint checkpoint,
        string? progressSummary,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.SaveCheckpoint(expectedRevision, generation, checkpoint, progressSummary, updatedAtUtc),
            cancellationToken));

    public ValueTask<WorkItem> CompleteAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string resultText,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.Complete(expectedRevision, generation, resultText, completedAtUtc),
            cancellationToken));

    public ValueTask<WorkItem> FailAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string failureCode,
        string failureSummary,
        bool replaySafe,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextRetryAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.Fail(expectedRevision, generation, failureCode, failureSummary, replaySafe, failedAtUtc, nextRetryAtUtc),
            cancellationToken));

    public ValueTask<WorkItem> RequestCancellationAsync(
        WorkOwner owner,
        Guid workItemId,
        long expectedRevision,
        string? knownEffectSummary,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.RequestCancellation(expectedRevision, knownEffectSummary, requestedAtUtc),
            cancellationToken,
            owner));

    public ValueTask<WorkItem> CommitCancellationAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string? knownEffectSummary,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.CommitCancellation(expectedRevision, generation, knownEffectSummary, cancelledAtUtc),
            cancellationToken));

    public async ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var asOf = asOfUtc.ToUnixTimeMilliseconds();
        var running = (int)WorkItemStatus.Running;
        var rows = await db.WorkItems
            .Where(item => item.Status == running && item.ClaimLeaseExpiresAtUtc <= asOf)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            var approval = await LoadApprovalAsync(db, row, cancellationToken).ConfigureAwait(false);
            var current = WorkStoreMapping.ToWorkItem(row, approval);
            WorkItem updated;
            try
            {
                updated = current.RecoverExpiredClaim(asOfUtc);
            }
            catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
            {
                throw WorkStoreMapping.Map(exception);
            }

            WorkStoreMapping.Apply(row, updated);
            await UpsertApprovalAsync(db, updated, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Work item revision is stale.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public ValueTask<WorkItem> BeginApprovalAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        Guid approvalId,
        string toolName,
        string preparedActionJson,
        string actionHash,
        string preview,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.BeginApproval(
                expectedRevision,
                generation,
                approvalId,
                toolName,
                preparedActionJson,
                actionHash,
                preview,
                expiresAtUtc,
                updatedAtUtc),
            cancellationToken));

    public ValueTask<WorkItem> DecideApprovalAsync(
        WorkOwner owner,
        Guid workItemId,
        Guid approvalId,
        long expectedRevision,
        long expectedApprovalRevision,
        string actionHash,
        WorkApprovalDecision decision,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.DecideApproval(approvalId, expectedRevision, expectedApprovalRevision, actionHash, decision, decidedAtUtc),
            cancellationToken,
            owner));

    public ValueTask<WorkItem> ExpireApprovalAsync(
        Guid workItemId,
        long expectedRevision,
        DateTimeOffset expiredAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(workItemId, item => item.ExpireApproval(expectedRevision, expiredAtUtc), cancellationToken));

    public ValueTask<WorkItem> MarkSideEffectAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkSideEffectDisposition disposition,
        string actionHash,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Required(MutateAsync(
            workItemId,
            item => item.MarkSideEffect(expectedRevision, generation, disposition, actionHash, updatedAtUtc),
            cancellationToken));

    private async ValueTask<WorkItem?> MutateAsync(
        Guid workItemId,
        Func<WorkItem, WorkItem> change,
        CancellationToken cancellationToken,
        WorkOwner? owner = null,
        bool notClaimable = false)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindRowAsync(db, workItemId, cancellationToken, tracked: true).ConfigureAwait(false);
        if (row is null || (owner is WorkOwner required && !OwnedBy(row, required)))
        {
            if (notClaimable)
            {
                return null;
            }

            throw AgentCoreErrors.NotFound("Work item was not found.");
        }

        var approval = await LoadApprovalAsync(db, row, cancellationToken).ConfigureAwait(false);
        var current = WorkStoreMapping.ToWorkItem(row, approval);
        WorkItem updated;
        try
        {
            updated = change(current);
        }
        catch (WorkItemTransitionException exception) when (notClaimable && exception.Failure == WorkTransitionFailure.NotClaimable)
        {
            return null;
        }
        catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
        {
            throw WorkStoreMapping.Map(exception);
        }

        if (!ReferenceEquals(updated, current))
        {
            WorkStoreMapping.Apply(row, updated);
            await UpsertApprovalAsync(db, updated, cancellationToken).ConfigureAwait(false);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw AgentCoreErrors.Conflict("Work item revision is stale.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async ValueTask<WorkItem> Required(ValueTask<WorkItem?> item)
    {
        var resolved = await item.ConfigureAwait(false);
        return resolved ?? throw AgentCoreErrors.NotFound("Work item was not found.");
    }

    private static async Task<WorkItemRecord?> FindRowAsync(
        AgentCoreDbContext db,
        Guid workItemId,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var id = workItemId.ToString("D");
        var query = tracked ? db.WorkItems : db.WorkItems.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.WorkItemId == id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkApprovalRecord?> LoadApprovalAsync(
        AgentCoreDbContext db,
        WorkItemRecord row,
        CancellationToken cancellationToken)
    {
        if (row.CurrentApprovalId is null)
        {
            return null;
        }

        var approval = await db.WorkApprovals
            .FirstOrDefaultAsync(item => item.ApprovalId == row.CurrentApprovalId, cancellationToken)
            .ConfigureAwait(false);
        return approval ?? throw AgentCoreErrors.Persistence("Work approval record is missing.");
    }

    private static async Task UpsertApprovalAsync(AgentCoreDbContext db, WorkItem item, CancellationToken cancellationToken)
    {
        if (item.Approval is null)
        {
            return;
        }

        var approvalId = item.Approval.ApprovalId.ToString("D");
        var row = await db.WorkApprovals
            .FirstOrDefaultAsync(approval => approval.ApprovalId == approvalId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            db.WorkApprovals.Add(WorkStoreMapping.ToApprovalRecord(item.Approval));
            return;
        }

        WorkStoreMapping.Apply(row, item.Approval);
    }

    private static async Task<IReadOnlyList<WorkItem>> MapAllAsync(
        AgentCoreDbContext db,
        List<WorkItemRecord> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var approvalIds = rows
            .Select(row => row.CurrentApprovalId)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        var approvals = approvalIds.Length == 0
            ? []
            : await db.WorkApprovals.AsNoTracking()
                .Where(approval => approvalIds.Contains(approval.ApprovalId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var byId = approvals.ToDictionary(approval => approval.ApprovalId, StringComparer.Ordinal);
        return rows.Select(row =>
        {
            WorkApprovalRecord? approval = null;
            if (row.CurrentApprovalId is not null && !byId.TryGetValue(row.CurrentApprovalId, out approval))
            {
                throw AgentCoreErrors.Persistence("Work approval record is missing.");
            }

            return WorkStoreMapping.ToWorkItem(row, approval);
        }).ToArray();
    }

    private static bool OwnedBy(WorkItemRecord row, WorkOwner owner) =>
        string.Equals(row.AgentInstanceId, owner.AgentInstanceId.ToString("D"), StringComparison.Ordinal)
        && string.Equals(row.ProfileId, owner.ProfileId.ToString("D"), StringComparison.Ordinal);

    private static int Clamp(int limit) => Math.Clamp(limit, 1, WorkLimits.MaxListLimit);

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode == 19;
}
