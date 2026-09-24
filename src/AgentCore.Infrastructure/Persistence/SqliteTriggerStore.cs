using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteTriggerStore(IDbContextFactory<AgentCoreDbContext> contexts) : ITriggerStore
{
    public async ValueTask<TriggerRegistration> CreateAsync(
        TriggerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.TriggerRegistrations.Add(TriggerStoreMapping.ToRecord(registration));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception))
        {
            throw AgentCoreErrors.Conflict("Trigger registration already exists.");
        }

        return registration;
    }

    public async ValueTask<TriggerRegistration?> GetAsync(
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : TriggerStoreMapping.ToRegistration(row);
    }

    public async ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(
        TriggerOwner owner,
        TriggerRegistrationStatus? status,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var query = db.TriggerRegistrations.AsNoTracking()
            .Where(row => row.AgentInstanceId == instanceId && row.ProfileId == profileId);
        if (status is not null)
        {
            var statusValue = (int)status.Value;
            query = query.Where(row => row.Status == statusValue);
        }

        var rows = await query
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.RegistrationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return await db.TriggerRegistrations.CountAsync(
            row => row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Status == (int)TriggerRegistrationStatus.Active,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TriggerRegistration> UpdateAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await FindRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var updated = TriggerRegistrationMutations.Update(
            current,
            expectedRevision,
            intent,
            schedule,
            nextOccurrenceAtUtc,
            expiresAtUtc,
            updatedAt);
        if (updated.Revision == current.Revision)
        {
            return current;
        }

        var id = registrationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var scheduleJson = TriggerScheduleCodec.Serialize(updated.Schedule);
        var rows = await db.TriggerRegistrations
            .Where(row => row.RegistrationId == id
                && row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Revision == expectedRevision
                && row.Status == (int)TriggerRegistrationStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Intent, updated.Intent)
                    .SetProperty(row => row.ScheduleKind, (int)updated.Schedule.Kind)
                    .SetProperty(row => row.ScheduleJson, scheduleJson)
                    .SetProperty(row => row.NextOccurrenceAtUtc, ToUnix(updated.NextOccurrenceAtUtc))
                    .SetProperty(row => row.ExpiresAtUtc, ToUnix(updated.ExpiresAtUtc))
                    .SetProperty(row => row.Revision, updated.Revision)
                    .SetProperty(row => row.ScheduleRevision, updated.ScheduleRevision)
                    .SetProperty(row => row.UpdatedAtUtc, updated.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows == 1)
        {
            return updated;
        }

        return await RejectStaleUpdateAsync(db, owner, registrationId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<TriggerRegistration> CancelAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await FindRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var cancelled = TriggerRegistrationMutations.Cancel(current, expectedRevision, cancelledAt);
        if (cancelled.Revision == current.Revision)
        {
            return current;
        }

        var id = registrationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var rows = await db.TriggerRegistrations
            .Where(row => row.RegistrationId == id
                && row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Revision == expectedRevision
                && row.Status == (int)TriggerRegistrationStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)TriggerRegistrationStatus.Cancelled)
                    .SetProperty(row => row.Revision, cancelled.Revision)
                    .SetProperty(row => row.UpdatedAtUtc, cancelled.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows == 1)
        {
            return cancelled;
        }

        var again = await FindRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (again is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var reloaded = TriggerStoreMapping.ToRegistration(again);
        if (reloaded.Status == TriggerRegistrationStatus.Cancelled)
        {
            return reloaded;
        }

        if (reloaded.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        throw AgentCoreErrors.Validation("Only an active registration can be cancelled.");
    }

    public async ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.Disposition != OccurrenceRoutingDisposition.Pending
            || occurrence.RoutingRevision != 0
            || occurrence.ClaimId is not null
            || occurrence.ClaimLeaseExpiresAtUtc is not null)
        {
            throw AgentCoreErrors.Validation("A new occurrence must start pending and unclaimed.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(occurrence));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new TriggerOccurrenceAdmitResult(TriggerOccurrenceAdmitKind.Admitted, occurrence);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception))
        {
            db.ChangeTracker.Clear();
            return await ResolveDuplicateAsync(db, occurrence, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyList<TriggerRegistration>> ListDueAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc).ToUnixTimeMilliseconds();
        var take = Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TriggerRegistrations.AsNoTracking()
            .Where(row => row.Status == (int)TriggerRegistrationStatus.Active
                && row.NextOccurrenceAtUtc != null
                && row.NextOccurrenceAtUtc <= asOf)
            .OrderBy(row => row.NextOccurrenceAtUtc)
            .ThenBy(row => row.RegistrationId)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedScheduleRevision,
        DateTimeOffset expectedNextOccurrenceAtUtc,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var expectedNext = TriggerScheduleCalculator.Truncate(expectedNextOccurrenceAtUtc);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)TriggerRegistrationStatus.Active
            || row.ScheduleRevision != expectedScheduleRevision
            || row.NextOccurrenceAtUtc != expectedNext.ToUnixTimeMilliseconds())
        {
            var current = row is null ? null : TriggerStoreMapping.ToRegistration(row);
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Stale, current, null, 0);
        }

        var currentRegistration = TriggerStoreMapping.ToRegistration(row);
        ScheduleAdmission decision;
        try
        {
            decision = TriggerScheduleAdmission.Decide(currentRegistration, asOf);
        }
        catch (TriggerTimeZoneUnavailableException)
        {
            var suspended = currentRegistration.WithScheduleAdvance(
                TriggerRegistrationStatus.SuspendedPolicy,
                null,
                currentRegistration.OccurrenceCount,
                currentRegistration.Revision + 1,
                asOf,
                "Timezone is unavailable.");
            ApplyAdvance(row, suspended);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Rejected, suspended, null, 0);
        }

        if (decision.Kind == ScheduleAdmissionKind.NotDue)
        {
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, currentRegistration, null, 0);
        }

        if (decision.Kind != ScheduleAdmissionKind.Admit)
        {
            var closed = TriggerScheduleAdmission.Advance(currentRegistration, decision, asOf);
            ApplyAdvance(row, closed);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var outcome = decision.Kind == ScheduleAdmissionKind.Expire
                ? ScheduledAdmitOutcome.Expired
                : ScheduledAdmitOutcome.Completed;
            return new ScheduledAdmitResult(outcome, closed, null, 0);
        }

        var occurrence = TriggerScheduleAdmission.CreateOccurrence(currentRegistration, decision, asOf);
        var advanced = TriggerScheduleAdmission.Advance(currentRegistration, decision, asOf);
        db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(occurrence));
        ApplyAdvance(row, advanced);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Admitted, advanced, occurrence, decision.SkippedCount);
        }
        catch (DbUpdateException exception) when (IsConstraint(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return await ResolveScheduledConflictAsync(
                db,
                owner,
                registrationId,
                expectedScheduleRevision,
                expectedNext,
                occurrence.DedupeKey,
                decision,
                asOf,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<TriggerOccurrence?> GetOccurrenceAsync(
        TriggerOwner owner,
        Guid occurrenceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = occurrenceId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var row = await db.TriggerOccurrences.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.OccurrenceId == id
                    && item.AgentInstanceId == instanceId
                    && item.ProfileId == profileId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : TriggerStoreMapping.ToOccurrence(row);
    }

    private static async Task<TriggerRegistrationRecord?> FindRowAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        var id = registrationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return await db.TriggerRegistrations.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.RegistrationId == id
                    && row.AgentInstanceId == instanceId
                    && row.ProfileId == profileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<TriggerRegistration> RejectStaleUpdateAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var again = await FindRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (again is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var reloaded = TriggerStoreMapping.ToRegistration(again);
        if (reloaded.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        throw AgentCoreErrors.Validation("Only an active registration can be updated.");
    }

    private static async Task<TriggerOccurrenceAdmitResult> ResolveDuplicateAsync(
        AgentCoreDbContext db,
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var existing = await FindByDedupeAsync(db, occurrence.Owner, occurrence.DedupeKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            throw AgentCoreErrors.Conflict("Occurrence identifier is already in use.");
        }

        return new TriggerOccurrenceAdmitResult(
            TriggerOccurrenceAdmitKind.Duplicate,
            TriggerStoreMapping.ToOccurrence(existing));
    }

    private static Task<TriggerOccurrenceRecord?> FindByDedupeAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return db.TriggerOccurrences.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.DedupeKey == dedupeKey
                    && row.AgentInstanceId == instanceId
                    && row.ProfileId == profileId,
                cancellationToken);
    }

    private static async Task<TriggerRegistrationRecord?> TrackRowAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        var id = registrationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return await db.TriggerRegistrations
            .FirstOrDefaultAsync(
                row => row.RegistrationId == id
                    && row.AgentInstanceId == instanceId
                    && row.ProfileId == profileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyAdvance(TriggerRegistrationRecord row, TriggerRegistration advanced)
    {
        row.Status = (int)advanced.Status;
        row.NextOccurrenceAtUtc = advanced.NextOccurrenceAtUtc?.ToUnixTimeMilliseconds();
        row.OccurrenceCount = advanced.OccurrenceCount;
        row.Revision = advanced.Revision;
        row.UpdatedAtUtc = advanced.Provenance.UpdatedAt.ToUnixTimeMilliseconds();
        row.SuspensionReason = advanced.SuspensionReason;
    }

    private static async Task<ScheduledAdmitResult> ResolveScheduledConflictAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid registrationId,
        long expectedScheduleRevision,
        DateTimeOffset expectedNext,
        string dedupeKey,
        ScheduleAdmission decision,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var existing = await FindByDedupeAsync(db, owner, dedupeKey, cancellationToken).ConfigureAwait(false);
        var registrationRow = await TrackRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (existing is null || registrationRow is null)
        {
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Stale, null, null, 0);
        }

        var mapped = TriggerStoreMapping.ToOccurrence(existing);

        if (registrationRow.Status == (int)TriggerRegistrationStatus.Active
            && registrationRow.ScheduleRevision == expectedScheduleRevision
            && registrationRow.NextOccurrenceAtUtc == expectedNext.ToUnixTimeMilliseconds())
        {
            var current = TriggerStoreMapping.ToRegistration(registrationRow);
            var advanced = TriggerScheduleAdmission.Advance(current, decision, asOf);
            ApplyAdvance(registrationRow, advanced);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Duplicate, advanced, mapped, decision.SkippedCount);
        }

        return new ScheduledAdmitResult(
            ScheduledAdmitOutcome.Duplicate,
            TriggerStoreMapping.ToRegistration(registrationRow),
            mapped,
            0);
    }

    private static long? ToUnix(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    public async ValueTask<TriggerRegistration?> SuspendPolicyAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string reason,
        DateTimeOffset suspendedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)TriggerRegistrationStatus.Active
            || row.Revision != expectedRevision)
        {
            return null;
        }

        var suspended = TriggerStoreMapping.ToRegistration(row).WithScheduleAdvance(
            TriggerRegistrationStatus.SuspendedPolicy,
            row.NextOccurrenceAtUtc is long nextMs
                ? DateTimeOffset.FromUnixTimeMilliseconds(nextMs)
                : null,
            row.OccurrenceCount,
            row.Revision + 1,
            suspendedAt,
            reason);
        row.Status = (int)TriggerRegistrationStatus.SuspendedPolicy;
        row.Revision = suspended.Revision;
        row.NextOccurrenceAtUtc = ToUnix(suspended.NextOccurrenceAtUtc);
        row.UpdatedAtUtc = suspended.Provenance.UpdatedAt.ToUnixTimeMilliseconds();
        row.SuspensionReason = suspended.SuspensionReason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return suspended;
    }

    public async ValueTask<TriggerRegistration?> TryReactivatePolicySuspensionAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset reactivatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, registrationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)TriggerRegistrationStatus.SuspendedPolicy
            || row.Revision != expectedRevision)
        {
            return null;
        }

        var current = TriggerStoreMapping.ToRegistration(row);
        DateTimeOffset? next = current.NextOccurrenceAtUtc;
        if (next is null && current.Schedule is OneShotSchedule oneShot)
        {
            next = oneShot.AtUtc;
        }

        next ??= TriggerScheduleCalculator.InitialNext(current.Schedule, reactivatedAt);
        var reactivated = current.WithScheduleAdvance(
            TriggerRegistrationStatus.Active,
            next,
            current.OccurrenceCount,
            current.Revision + 1,
            reactivatedAt,
            null);
        row.Status = (int)TriggerRegistrationStatus.Active;
        row.Revision = reactivated.Revision;
        row.NextOccurrenceAtUtc = ToUnix(reactivated.NextOccurrenceAtUtc);
        row.UpdatedAtUtc = reactivated.Provenance.UpdatedAt.ToUnixTimeMilliseconds();
        row.SuspensionReason = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return reactivated;
    }

    public ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(occurrenceId, current =>
        {
            var expired = current.Disposition == OccurrenceRoutingDisposition.Claimed
                && current.ClaimLeaseExpiresAtUtc is DateTimeOffset lease
                && lease <= claimedAt;
            if (current.Disposition != OccurrenceRoutingDisposition.Pending && !expired)
            {
                return null;
            }

            return current.WithRouting(
                OccurrenceRoutingDisposition.Claimed,
                null,
                current.RoutingRevision + 1,
                claimedAt,
                claimId,
                leaseExpiresAtUtc);
        }, cancellationToken);

    public ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.LivePrepared,
                    null,
                    current.RoutingRevision + 1,
                    acceptedAt,
                    null,
                    acceptedAt.Add(TriggerOccurrenceRouter.LivePreparedLease))
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> ConfirmLiveBeginAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.LivePrepared
                && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(OccurrenceRoutingDisposition.AcceptedLive, null, current.RoutingRevision + 1, confirmedAt, null, null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> RevertLivePreparedAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.LivePrepared
                && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(OccurrenceRoutingDisposition.Pending, null, current.RoutingRevision + 1, revertedAt, null, null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> PromoteLivePreparedAwaitingDurableWorkAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.LivePrepared
                && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.AwaitingDurableWork,
                    reason,
                    current.RoutingRevision + 1,
                    markedAt,
                    null,
                    null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> RevertAcceptedLiveAsync(
        Guid occurrenceId,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.LivePrepared
                ? current.WithRouting(OccurrenceRoutingDisposition.Pending, null, current.RoutingRevision + 1, revertedAt, null, null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> ReleaseClaimAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(OccurrenceRoutingDisposition.Pending, null, current.RoutingRevision + 1, releasedAt, null, null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> MarkAwaitingDurableWorkAsync(
        Guid occurrenceId,
        Guid claimId,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, reason, current.RoutingRevision + 1, markedAt, null, null)
                : null,
            cancellationToken);

    public ValueTask<TriggerOccurrence?> TryRejectPendingAsync(
        Guid occurrenceId,
        string reason,
        DateTimeOffset rejectedAt,
        CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(
            occurrenceId,
            current => current.Disposition == OccurrenceRoutingDisposition.Pending
                ? current.WithRouting(OccurrenceRoutingDisposition.Rejected, reason, current.RoutingRevision + 1, rejectedAt, null, null)
                : null,
            cancellationToken);

    public async ValueTask<int> RecoverExpiredClaimsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TriggerOccurrences
            .Where(row => row.Disposition == (int)OccurrenceRoutingDisposition.Claimed
                && row.ClaimLeaseExpiresAtUtc != null
                && row.ClaimLeaseExpiresAtUtc <= asOf.ToUnixTimeMilliseconds())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            var current = TriggerStoreMapping.ToOccurrence(row);
            var next = current.WithRouting(
                OccurrenceRoutingDisposition.Pending,
                null,
                current.RoutingRevision + 1,
                asOf,
                null,
                null);
            CopyRouting(row, next);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public async ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(
        OccurrenceRoutingDisposition disposition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TriggerOccurrences.AsNoTracking()
            .Where(row => row.Disposition == (int)disposition)
            .OrderBy(row => row.AdmittedAtUtc)
            .ThenBy(row => row.OccurrenceId)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToOccurrence).ToArray();
    }

    private async ValueTask<TriggerOccurrence?> MutateOccurrenceAsync(
        Guid occurrenceId,
        Func<TriggerOccurrence, TriggerOccurrence?> change,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = occurrenceId.ToString("D");
        var row = await db.TriggerOccurrences.FirstOrDefaultAsync(item => item.OccurrenceId == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var next = change(TriggerStoreMapping.ToOccurrence(row));
        if (next is null)
        {
            return null;
        }

        CopyRouting(row, next);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static void CopyRouting(TriggerOccurrenceRecord row, TriggerOccurrence next)
    {
        row.Disposition = (int)next.Disposition;
        row.DispositionReason = next.DispositionReason;
        row.RoutingRevision = next.RoutingRevision;
        row.RoutingUpdatedAtUtc = next.RoutingUpdatedAtUtc?.ToUnixTimeMilliseconds();
        row.ClaimId = next.ClaimId?.ToString("D");
        row.ClaimLeaseExpiresAtUtc = next.ClaimLeaseExpiresAtUtc?.ToUnixTimeMilliseconds();
    }

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19;
}

internal static class TriggerStoreMapping
{
    public static TriggerRegistrationRecord ToRecord(TriggerRegistration registration) => new()
    {
        RegistrationId = registration.RegistrationId.ToString("D"),
        AgentInstanceId = registration.Owner.AgentInstanceId.ToString("D"),
        ProfileId = registration.Owner.ProfileId.ToString("D"),
        Status = (int)registration.Status,
        Intent = registration.Intent,
        ScheduleKind = (int)registration.Schedule.Kind,
        ScheduleJson = TriggerScheduleCodec.Serialize(registration.Schedule),
        NextOccurrenceAtUtc = registration.NextOccurrenceAtUtc?.ToUnixTimeMilliseconds(),
        ExpiresAtUtc = registration.ExpiresAtUtc?.ToUnixTimeMilliseconds(),
        OccurrenceCount = registration.OccurrenceCount,
        Revision = registration.Revision,
        ScheduleRevision = registration.ScheduleRevision,
        AuthorizationOrigin = (int)registration.Provenance.AuthorizationOrigin,
        SourceSessionId = registration.Provenance.SourceSessionId?.ToString("D"),
        SourceEventId = registration.Provenance.SourceEventId?.ToString("D"),
        CreatedAtUtc = registration.Provenance.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUtc = registration.Provenance.UpdatedAt.ToUnixTimeMilliseconds(),
        SuspensionReason = registration.SuspensionReason
    };

    public static TriggerRegistration ToRegistration(TriggerRegistrationRecord row) => new(
        Guid.Parse(row.RegistrationId),
        new TriggerOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
        (TriggerRegistrationStatus)row.Status,
        row.Intent,
        TriggerScheduleCodec.Deserialize(row.ScheduleJson),
        FromUnix(row.NextOccurrenceAtUtc),
        FromUnix(row.ExpiresAtUtc),
        row.OccurrenceCount,
        row.Revision,
        row.ScheduleRevision,
        new TriggerProvenance(
            (TriggerAuthorizationOrigin)row.AuthorizationOrigin,
            ParseOptional(row.SourceSessionId),
            ParseOptional(row.SourceEventId),
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc)),
        row.SuspensionReason);

    public static TriggerOccurrenceRecord ToRecord(TriggerOccurrence occurrence) => new()
    {
        OccurrenceId = occurrence.OccurrenceId.ToString("D"),
        DedupeKey = occurrence.DedupeKey,
        RegistrationId = occurrence.RegistrationId?.ToString("D"),
        AgentInstanceId = occurrence.Owner.AgentInstanceId.ToString("D"),
        ProfileId = occurrence.Owner.ProfileId.ToString("D"),
        SourceKind = (int)occurrence.SourceKind,
        ScheduledAtUtc = occurrence.ScheduledAtUtc?.ToUnixTimeMilliseconds(),
        ObservedAtUtc = occurrence.ObservedAtUtc.ToUnixTimeMilliseconds(),
        AdmittedAtUtc = occurrence.AdmittedAtUtc.ToUnixTimeMilliseconds(),
        EvidenceJson = occurrence.EvidenceJson,
        SourceEventId = occurrence.SourceEventId?.ToString("D"),
        ScheduleRevision = occurrence.ScheduleRevision,
        Disposition = (int)occurrence.Disposition,
        DispositionReason = occurrence.DispositionReason,
        RoutingRevision = occurrence.RoutingRevision,
        RoutingUpdatedAtUtc = occurrence.RoutingUpdatedAtUtc?.ToUnixTimeMilliseconds(),
        ClaimId = occurrence.ClaimId?.ToString("D"),
        ClaimLeaseExpiresAtUtc = occurrence.ClaimLeaseExpiresAtUtc?.ToUnixTimeMilliseconds()
    };

    public static TriggerOccurrence ToOccurrence(TriggerOccurrenceRecord row) => new(
        Guid.Parse(row.OccurrenceId),
        row.DedupeKey,
        ParseOptional(row.RegistrationId),
        new TriggerOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
        (TriggerSourceKind)row.SourceKind,
        FromUnix(row.ScheduledAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.ObservedAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAtUtc),
        row.EvidenceJson,
        ParseOptional(row.SourceEventId),
        row.ScheduleRevision,
        (OccurrenceRoutingDisposition)row.Disposition,
        row.DispositionReason,
        row.RoutingRevision,
        FromUnix(row.RoutingUpdatedAtUtc),
        ParseOptional(row.ClaimId),
        FromUnix(row.ClaimLeaseExpiresAtUtc));

    private static DateTimeOffset? FromUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static Guid? ParseOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
}
