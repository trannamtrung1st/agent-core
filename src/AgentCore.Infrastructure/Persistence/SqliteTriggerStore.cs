using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteTriggerStore(IDbContextFactory<AgentCoreDbContext> contexts) : ITriggerStore
{
    public async ValueTask<Automation> SaveAutomationAsync(Automation proposed, long expectedRevision,
        AgentCore.Application.Admin.AdminEventAppend history, CancellationToken ct = default, int maxActiveRegistrations = 32)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await TrackRowAsync(db, proposed.Owner, proposed.AutomationId, ct);
        var current = row is null ? null : TriggerStoreMapping.ToRegistration(row);
        AutomationRules.ValidateSave(current, proposed, expectedRevision);
        if (proposed.Status == AutomationStatus.Active && current?.Status != AutomationStatus.Active && await ActiveSchedules(db, proposed.Owner).CountAsync(ct) >= maxActiveRegistrations)
            throw AgentCoreErrors.Validation("Active schedule limit has been reached.");
        if (row is null) db.Automations.Add(TriggerStoreMapping.ToRecord(proposed));
        else db.Entry(row).CurrentValues.SetValues(TriggerStoreMapping.ToRecord(proposed));
        AdminEventPersistence.StageAppend(db, history, history.OperationId);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Registration revision is stale."); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw AgentCoreErrors.Conflict("Registration already exists."); }
        return proposed;
    }
    public async ValueTask<ScheduledAdmitResult> AdmitAutomationNowAsync(Automation registration, ExecutionModelPin pin,
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await TrackRowAsync(db, registration.Owner, registration.AutomationId, ct);
        if (row?.Revision != registration.Revision || row.Status != (int)AutomationStatus.Active)
            return new(ScheduledAdmitOutcome.Stale, row is null ? null : TriggerStoreMapping.ToRegistration(row), null, 0);
        if (await RegistrationBusyAsync(db, registration.AutomationId, ct))
            return new(ScheduledAdmitOutcome.NotDue, registration, null, 0);
        var occurrence = AutomationRules.ManualOccurrence(TriggerStoreMapping.ToRegistration(row), pin, asOf);

        var existing = await FindByDedupeAsync(db, registration.Owner, occurrence.DedupeKey, ct);
        if (existing is not null) return new(ScheduledAdmitOutcome.Duplicate, registration, TriggerStoreMapping.ToOccurrence(existing), 0);
        db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(occurrence));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(ScheduledAdmitOutcome.Admitted, registration, occurrence, 0);
    }
    private static Task<bool> RegistrationBusyAsync(AgentCoreDbContext db, Guid automationId, CancellationToken ct)
    {
        var id = automationId.ToString("D");
        return db.TriggerOccurrences.AnyAsync(o => o.AutomationId == id && (o.Disposition == (int)OccurrenceRoutingDisposition.Pending || o.Disposition == (int)OccurrenceRoutingDisposition.Claimed
                || o.Disposition == (int)OccurrenceRoutingDisposition.AwaitingDurableWork
                || db.AgentRuns.Any(run => run.AgentRunId == o.AcceptedAgentRunId
                    && (run.Status == (int)AgentCore.Domain.Conversation.AgentRunStatus.Queued
                        || run.Status == (int)AgentCore.Domain.Conversation.AgentRunStatus.Running
                        || run.Status == (int)AgentCore.Domain.Conversation.AgentRunStatus.WaitingForApproval
                        || run.Status == (int)AgentCore.Domain.Conversation.AgentRunStatus.WaitingToRetry))), ct);
    }

    public async ValueTask<Automation> CreateAsync(
        Automation registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Automations.Add(TriggerStoreMapping.ToRecord(registration));
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

    public async ValueTask<Automation?> GetAsync(
        TriggerOwner owner,
        Guid automationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : TriggerStoreMapping.ToRegistration(row);
    }

    public async ValueTask<IReadOnlyList<Automation>> ListAsync(
        TriggerOwner owner,
        AutomationStatus? status,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var query = db.Automations.AsNoTracking()
            .Where(row => row.AgentInstanceId == instanceId && row.ProfileId == profileId);
        if (status is not null)
        {
            var statusValue = (int)status.Value;
            query = query.Where(row => row.Status == statusValue);
        }

        var rows = await query
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.AutomationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<IReadOnlyList<Automation>> ListAutomationsPageAsync(
        TriggerOwner owner, int limit, Guid? before, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var query = db.Automations.AsNoTracking()
            .Where(item => item.AgentInstanceId == instanceId && item.ProfileId == profileId && item.EventSourceId == null);
        if (before is Guid id)
        {
            var anchorId = id.ToString("D");
            var anchor = await query.FirstOrDefaultAsync(item => item.AutomationId == anchorId, cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Page cursor was not found.");
            query = query.Where(item => item.CreatedAtUtc < anchor.CreatedAtUtc ||
                item.CreatedAtUtc == anchor.CreatedAtUtc && string.Compare(item.AutomationId, anchorId) < 0);
        }
        var rows = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.AutomationId)
            .Take(Math.Clamp(limit, 1, 100)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<IReadOnlyList<Automation>> ListSuspendedPolicyForAgentInstanceAsync(
        Guid agentInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = agentInstanceId.ToString("D");
        var suspended = (int)AutomationStatus.SuspendedPolicy;
        var rows = await db.Automations.AsNoTracking()
            .Where(row => row.AgentInstanceId == instanceId && row.Status == suspended)
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.AutomationId)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<IReadOnlyList<Automation>> ListFutureRegistrationsForAgentInstanceAsync(
        Guid agentInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = agentInstanceId.ToString("D");
        var active = (int)AutomationStatus.Active;
        var suspended = (int)AutomationStatus.SuspendedPolicy;
        var rows = await db.Automations.AsNoTracking()
            .Where(row => row.AgentInstanceId == instanceId && (row.Status == active || row.Status == suspended))
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.AutomationId)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    private static IQueryable<AutomationRecord> ActiveSchedules(AgentCoreDbContext db, TriggerOwner owner)
    {
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return db.Automations.Where(row => row.AgentInstanceId == instanceId && row.ProfileId == profileId
            && row.Status == (int)AutomationStatus.Active );
    }

    public async ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ActiveSchedules(db, owner).CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Automation>> ListEventSubscriptionsAsync(
        Guid eventSourceId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sourceId = eventSourceId.ToString("D");
        var rows = await db.Automations.AsNoTracking()
            .Where(row => row.Status == (int)AutomationStatus.Active
                && row.EventSourceId == sourceId
                && row.EventType == eventType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public async ValueTask<Automation> UpdateAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var updated = AutomationMutations.Update(
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

        var id = automationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var scheduleJson = TriggerScheduleCodec.Serialize(updated.Schedule);
        var rows = await db.Automations
            .Where(row => row.AutomationId == id
                && row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Revision == expectedRevision
                && row.Status == (int)AutomationStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Instructions, updated.Instructions)
                    .SetProperty(row => row.TriggerKind, (int)updated.Trigger.Kind)
                    .SetProperty(row => row.ScheduleJson, scheduleJson)
                    .SetProperty(row => row.NextOccurrenceAtUtc, ToUnix(updated.NextOccurrenceAtUtc))
                    .SetProperty(row => row.ExpiresAtUtc, ToUnix(updated.ExpiresAtUtc))
                    .SetProperty(row => row.Revision, updated.Revision)
                    .SetProperty(row => row.TriggerRevision, updated.TriggerRevision)
                    .SetProperty(row => row.UpdatedAtUtc, updated.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows == 1)
        {
            return updated;
        }

        return await RejectStaleUpdateAsync(db, owner, automationId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Automation> SetModelOverrideAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var updated = AutomationMutations.SetModelOverride(
            current,
            expectedRevision,
            catalogKey,
            reasoningEffort,
            updatedAt);
        if (updated.Revision == current.Revision)
        {
            return current;
        }

        var id = automationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var rows = await db.Automations
            .Where(row => row.AutomationId == id
                && row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Revision == expectedRevision
                && row.Status == (int)AutomationStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.ModelOverrideCatalogKey, updated.ModelOverrideCatalogKey)
                    .SetProperty(row => row.ModelOverrideReasoningEffort, updated.ModelOverrideReasoningEffort)
                    .SetProperty(row => row.Revision, updated.Revision)
                    .SetProperty(row => row.UpdatedAtUtc, updated.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows == 1)
        {
            return updated;
        }

        return await RejectStaleUpdateAsync(db, owner, automationId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Automation> CancelAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var cancelled = AutomationMutations.Cancel(current, expectedRevision, cancelledAt);
        if (cancelled.Revision == current.Revision)
        {
            return current;
        }

        var id = automationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        var rows = await db.Automations
            .Where(row => row.AutomationId == id
                && row.AgentInstanceId == instanceId
                && row.ProfileId == profileId
                && row.Revision == expectedRevision
                && (row.Status == (int)AutomationStatus.Active
                    || row.Status == (int)AutomationStatus.SuspendedPolicy))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)AutomationStatus.Cancelled)
                    .SetProperty(row => row.Revision, cancelled.Revision)
                    .SetProperty(row => row.UpdatedAtUtc, cancelled.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows == 1)
        {
            return cancelled;
        }

        var again = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (again is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var reloaded = TriggerStoreMapping.ToRegistration(again);
        if (reloaded.Status == AutomationStatus.Cancelled)
        {
            return reloaded;
        }

        if (reloaded.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        throw AgentCoreErrors.Validation("Only an active or policy-suspended registration can be cancelled.");
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

    public async ValueTask<IReadOnlyList<Automation>> ListDueAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc).ToUnixTimeMilliseconds();
        var take = Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Automations.AsNoTracking()
            .Where(row => row.Status == (int)AutomationStatus.Active
                && row.NextOccurrenceAtUtc != null
                && row.NextOccurrenceAtUtc <= asOf)
            .OrderBy(row => row.NextOccurrenceAtUtc)
            .ThenBy(row => row.AutomationId)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToRegistration).ToArray();
    }

    public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(TriggerOwner owner, Guid automationId,
        long expectedTriggerRevision, DateTimeOffset expectedNextOccurrenceAtUtc, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default) => TryAdmitScheduledCoreAsync(owner, automationId,
            expectedTriggerRevision, expectedNextOccurrenceAtUtc, asOfUtc, cancellationToken);

    private async ValueTask<ScheduledAdmitResult> TryAdmitScheduledCoreAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedTriggerRevision,
        DateTimeOffset expectedNextOccurrenceAtUtc,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default, ExecutionModelPin? pin = null, long? expectedRevision = null)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var expectedNext = TriggerScheduleCalculator.Truncate(expectedNextOccurrenceAtUtc);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)AutomationStatus.Active
            || (expectedRevision is not null && row.Revision != expectedRevision)
            || row.TriggerRevision != expectedTriggerRevision
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
                AutomationStatus.SuspendedPolicy,
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

        if ( await RegistrationBusyAsync(db, automationId, cancellationToken))
        {
            var coalesced = TriggerScheduleAdmission.Advance(currentRegistration,
                decision with { OccurrenceCount = currentRegistration.OccurrenceCount }, asOf);
            ApplyAdvance(row, coalesced);
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            AgentCore.Application.Observability.RuntimeTelemetry.RecordWork("coalesced");
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, coalesced, null, decision.SkippedCount + 1);
        }
        var occurrence = TriggerScheduleAdmission.CreateOccurrence(currentRegistration, decision, asOf);
        if (pin is not null) occurrence = occurrence.WithModelPin(pin);
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
                automationId,
                expectedTriggerRevision,
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

    private static async Task<AutomationRecord?> FindRowAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid automationId,
        CancellationToken cancellationToken)
    {
        var id = automationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return await db.Automations.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.AutomationId == id
                    && row.AgentInstanceId == instanceId
                    && row.ProfileId == profileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Automation> RejectStaleUpdateAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var again = await FindRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
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

    private static async Task<AutomationRecord?> TrackRowAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid automationId,
        CancellationToken cancellationToken)
    {
        var id = automationId.ToString("D");
        var instanceId = owner.AgentInstanceId.ToString("D");
        var profileId = owner.ProfileId.ToString("D");
        return await db.Automations
            .FirstOrDefaultAsync(
                row => row.AutomationId == id
                    && row.AgentInstanceId == instanceId
                    && row.ProfileId == profileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyAdvance(AutomationRecord row, Automation advanced)
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
        Guid automationId,
        long expectedTriggerRevision,
        DateTimeOffset expectedNext,
        string dedupeKey,
        ScheduleAdmission decision,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var existing = await FindByDedupeAsync(db, owner, dedupeKey, cancellationToken).ConfigureAwait(false);
        var registrationRow = await TrackRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (existing is null || registrationRow is null)
        {
            return new ScheduledAdmitResult(ScheduledAdmitOutcome.Stale, null, null, 0);
        }

        var mapped = TriggerStoreMapping.ToOccurrence(existing);

        if (registrationRow.Status == (int)AutomationStatus.Active
            && registrationRow.TriggerRevision == expectedTriggerRevision
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

    public async ValueTask<Automation?> SuspendPolicyAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string reason,
        DateTimeOffset suspendedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)AutomationStatus.Active
            || row.Revision != expectedRevision)
        {
            return null;
        }

        var suspended = TriggerStoreMapping.ToRegistration(row).WithScheduleAdvance(
            AutomationStatus.SuspendedPolicy,
            row.NextOccurrenceAtUtc is long nextMs
                ? DateTimeOffset.FromUnixTimeMilliseconds(nextMs)
                : null,
            row.OccurrenceCount,
            row.Revision + 1,
            suspendedAt,
            reason);
        row.Status = (int)AutomationStatus.SuspendedPolicy;
        row.Revision = suspended.Revision;
        row.NextOccurrenceAtUtc = ToUnix(suspended.NextOccurrenceAtUtc);
        row.UpdatedAtUtc = suspended.Provenance.UpdatedAt.ToUnixTimeMilliseconds();
        row.SuspensionReason = suspended.SuspensionReason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return suspended;
    }

    public async ValueTask<Automation?> TryReactivatePolicySuspensionAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        DateTimeOffset reactivatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await TrackRowAsync(db, owner, automationId, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != (int)AutomationStatus.SuspendedPolicy
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
            AutomationStatus.Active,
            next,
            current.OccurrenceCount,
            current.Revision + 1,
            reactivatedAt,
            null);
        row.Status = (int)AutomationStatus.Active;
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

    public ValueTask<TriggerOccurrence?> BindLiveSessionAsync(Guid occurrenceId, long expectedRoutingRevision,
        Guid sessionId, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
        MutateOccurrenceAsync(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.LivePrepared && current.RoutingRevision == expectedRoutingRevision
                ? current.WithLiveSession(sessionId, expectedRoutingRevision, atUtc) : null, cancellationToken);

    public async ValueTask<TriggerOccurrence?> CompleteLiveEvaluationAsync(Guid occurrenceId, Guid sessionId,
        DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        // Begin confirmation may win between the read and quiet settlement. Re-read once;
        // a Run admission that wins the same CAS is already a settled receipt.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await MutateOccurrenceAsync(occurrenceId, current => current.LiveSessionId == sessionId
                    && current.Disposition is OccurrenceRoutingDisposition.LivePrepared or OccurrenceRoutingDisposition.AcceptedLive
                    && current.LiveEvaluationCompletedAtUtc is null
                        ? current.WithLiveEvaluation(sessionId, null, atUtc) : null, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0) { }
        }
    }

    public async ValueTask<IReadOnlyList<TriggerOccurrence>> ListUnsettledLiveAsync(int limit, Guid? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw AgentCoreErrors.Validation("Live repair limit is invalid.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cursor = after?.ToString("D");
        var rows = await db.TriggerOccurrences.AsNoTracking().Where(row => row.Disposition == (int)OccurrenceRoutingDisposition.AcceptedLive
            && row.LiveSessionId != null && row.LiveEvaluationCompletedAtUtc == null
            && (cursor == null || string.Compare(row.OccurrenceId, cursor) > 0))
            .OrderBy(row => row.OccurrenceId).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(TriggerStoreMapping.ToOccurrence).ToArray();
    }

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

    public async ValueTask<TriggerOccurrence?> RejectAwaitingDurableWorkAsync(Guid occurrenceId, long expectedRevision, string reason, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.TriggerOccurrences.SingleOrDefaultAsync(r => r.OccurrenceId == occurrenceId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null || row.Disposition != (int)OccurrenceRoutingDisposition.AwaitingDurableWork || row.RoutingRevision != expectedRevision) return null;
        var occurrence = TriggerStoreMapping.ToOccurrence(row);
        var rejected = occurrence.WithRouting(OccurrenceRoutingDisposition.Rejected, reason, occurrence.RoutingRevision + 1, atUtc, null, null);
        TriggerStoreMapping.CopyRouting(row, rejected);
        if (row.AutomationId is { } id && await db.Automations.SingleOrDefaultAsync(a => a.AutomationId == id, ct).ConfigureAwait(false) is { } automation)
        {
            var current = TriggerStoreMapping.ToRegistration(automation);
            if (current.ExecutionTarget == occurrence.ExecutionTarget && current.Status is not (AutomationStatus.Disabled or AutomationStatus.Cancelled))
            {
                var recurring = current.Trigger is not ScheduleTrigger { Schedule: OneShotSchedule };
                automation.Status = recurring ? (int)AutomationStatus.SuspendedPolicy : automation.Status;
                automation.NextOccurrenceAtUtc = recurring ? null : automation.NextOccurrenceAtUtc;
                automation.SuspensionReason = reason;
                automation.Revision++;
                automation.UpdatedAtUtc = atUtc.ToUnixTimeMilliseconds();
            }
        }
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { return null; }
        return rejected;
    }

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
            TriggerStoreMapping.CopyRouting(row, next);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public async ValueTask<TriggerOccurrence?> TryAssignModelPinIfMissingAsync(
        Guid occurrenceId,
        ExecutionModelPin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = occurrenceId.ToString("D");
        var row = await db.TriggerOccurrences.FirstOrDefaultAsync(item => item.OccurrenceId == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(row.ModelCatalogKey))
        {
            return TriggerStoreMapping.ToOccurrence(row);
        }

        row.ModelCatalogKey = pin.CatalogKey;
        row.ModelProviderAlias = pin.ProviderAlias;
        row.ModelId = pin.ModelId;
        row.ModelReasoningEffort = pin.ReasoningEffort;
        row.ModelSource = (int)pin.Source;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TriggerStoreMapping.ToOccurrence(row);
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

        TriggerStoreMapping.CopyRouting(row, next);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static bool IsConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19;
}

internal static class TriggerStoreMapping
{
    public static AutomationRecord ToRecord(Automation registration) => new()
    {
        ExecutionTargetKind = (int)registration.ExecutionTarget.Kind,
        TargetSessionId = registration.ExecutionTarget.SessionId?.ToString("D"),
        ReportToSessionId = registration.CompletionDelivery.SessionId?.ToString("D"),
        AutomationId = registration.AutomationId.ToString("D"),
        AgentInstanceId = registration.Owner.AgentInstanceId.ToString("D"),
        ProfileId = registration.Owner.ProfileId.ToString("D"),
        Status = (int)registration.Status,
        Instructions = registration.Instructions,
        Name = registration.Name,
        TriggerKind = (int)registration.Trigger.Kind,
        ScheduleJson = registration.Trigger is ScheduleTrigger s ? TriggerScheduleCodec.Serialize(s.Schedule) : null,
        NextOccurrenceAtUtc = registration.NextOccurrenceAtUtc?.ToUnixTimeMilliseconds(),
        ExpiresAtUtc = registration.ExpiresAtUtc?.ToUnixTimeMilliseconds(),
        OccurrenceCount = registration.OccurrenceCount,
        Revision = registration.Revision,
        TriggerRevision = registration.TriggerRevision,
        AuthorizationOrigin = (int)registration.Provenance.AuthorizationOrigin,
        SourceSessionId = registration.Provenance.SourceSessionId?.ToString("D"),
        SourceEventId = registration.Provenance.SourceEventId?.ToString("D"),
        CreatedAtUtc = registration.Provenance.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUtc = registration.Provenance.UpdatedAt.ToUnixTimeMilliseconds(),
        SuspensionReason = registration.SuspensionReason,
        ModelOverrideCatalogKey = registration.ModelOverrideCatalogKey,
        ModelOverrideReasoningEffort = registration.ModelOverrideReasoningEffort,
        RequiresVision = registration.RequiresVision,
        RequiresTools = registration.RequiresTools,
        EventSourceId = registration.EventSourceId?.ToString("D"),
        EventType = registration.EventType
    };

    public static Automation ToRegistration(AutomationRecord row) => new(
        Guid.Parse(row.AutomationId),
        new TriggerOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
        (AutomationStatus)row.Status,
        row.Instructions,
        row.TriggerKind == (int)AutomationTriggerKind.Schedule ? new ScheduleTrigger(TriggerScheduleCodec.Deserialize(row.ScheduleJson!)) : new EventTrigger(Guid.Parse(row.EventSourceId!), row.EventType!),
        FromUnix(row.NextOccurrenceAtUtc),
        FromUnix(row.ExpiresAtUtc),
        row.OccurrenceCount,
        row.Revision,
        row.TriggerRevision,
        new TriggerProvenance(
            (TriggerAuthorizationOrigin)row.AuthorizationOrigin,
            ParseOptional(row.SourceSessionId),
            ParseOptional(row.SourceEventId),
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc)),
        row.SuspensionReason,
        row.ModelOverrideCatalogKey,
        row.ModelOverrideReasoningEffort,
        row.RequiresVision,
        row.Name, new((AutomationExecutionTargetKind)row.ExecutionTargetKind, ParseOptional(row.TargetSessionId)),
        new(ParseOptional(row.ReportToSessionId)), row.RequiresTools);

    public static TriggerOccurrenceRecord ToRecord(TriggerOccurrence occurrence) => new()
    {
        ExecutionTargetKind = (int)occurrence.ExecutionTarget.Kind,
        TargetSessionId = occurrence.ExecutionTarget.SessionId?.ToString("D"),
        ReportToSessionId = occurrence.CompletionDelivery.SessionId?.ToString("D"),
        OccurrenceId = occurrence.OccurrenceId.ToString("D"),
        DedupeKey = occurrence.DedupeKey,
        AutomationId = occurrence.AutomationId?.ToString("D"),
        AgentInstanceId = occurrence.Owner.AgentInstanceId.ToString("D"),
        ProfileId = occurrence.Owner.ProfileId.ToString("D"),
        SourceKind = (int)occurrence.SourceKind,
        ScheduledAtUtc = occurrence.ScheduledAtUtc?.ToUnixTimeMilliseconds(),
        ObservedAtUtc = occurrence.ObservedAtUtc.ToUnixTimeMilliseconds(),
        AdmittedAtUtc = occurrence.AdmittedAtUtc.ToUnixTimeMilliseconds(),
        EvidenceJson = occurrence.EvidenceJson,
        SourceEventId = occurrence.SourceEventId?.ToString("D"),
        TriggerRevision = occurrence.TriggerRevision,
        Disposition = (int)occurrence.Disposition,
        DispositionReason = occurrence.DispositionReason,
        RoutingRevision = occurrence.RoutingRevision,
        RoutingUpdatedAtUtc = occurrence.RoutingUpdatedAtUtc?.ToUnixTimeMilliseconds(),
        ClaimId = occurrence.ClaimId?.ToString("D"),
        ClaimLeaseExpiresAtUtc = occurrence.ClaimLeaseExpiresAtUtc?.ToUnixTimeMilliseconds(),
        ExecutionSessionId = occurrence.ExecutionSessionId?.ToString("D"),
        AcceptedAgentRunId = occurrence.AcceptedAgentRunId?.ToString("D"),
        LiveSessionId = occurrence.LiveSessionId?.ToString("D"),
        LiveEvaluationCompletedAtUtc = occurrence.LiveEvaluationCompletedAtUtc?.ToUnixTimeMilliseconds(),
        ModelCatalogKey = occurrence.ModelPin?.CatalogKey,
        ModelProviderAlias = occurrence.ModelPin?.ProviderAlias,
        ModelId = occurrence.ModelPin?.ModelId,
        ModelReasoningEffort = occurrence.ModelPin?.ReasoningEffort,
        ModelSource = occurrence.ModelPin is null ? null : (int)occurrence.ModelPin.Source
    };

    public static void CopyRouting(TriggerOccurrenceRecord row, TriggerOccurrence next)
    {
        row.Disposition = (int)next.Disposition;
        row.DispositionReason = next.DispositionReason;
        row.RoutingRevision = next.RoutingRevision;
        row.RoutingUpdatedAtUtc = next.RoutingUpdatedAtUtc?.ToUnixTimeMilliseconds();
        row.ClaimId = next.ClaimId?.ToString("D");
        row.ClaimLeaseExpiresAtUtc = next.ClaimLeaseExpiresAtUtc?.ToUnixTimeMilliseconds();
        row.ExecutionSessionId = next.ExecutionSessionId?.ToString("D");
        row.AcceptedAgentRunId = next.AcceptedAgentRunId?.ToString("D");
        row.LiveSessionId = next.LiveSessionId?.ToString("D");
        row.LiveEvaluationCompletedAtUtc = next.LiveEvaluationCompletedAtUtc?.ToUnixTimeMilliseconds();
    }

    public static TriggerOccurrence ToOccurrence(TriggerOccurrenceRecord row) => new(
        Guid.Parse(row.OccurrenceId),
        row.DedupeKey,
        ParseOptional(row.AutomationId),
        new TriggerOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
        (TriggerSourceKind)row.SourceKind,
        FromUnix(row.ScheduledAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.ObservedAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAtUtc),
        row.EvidenceJson,
        ParseOptional(row.SourceEventId),
        row.TriggerRevision,
        (OccurrenceRoutingDisposition)row.Disposition,
        row.DispositionReason,
        row.RoutingRevision,
        FromUnix(row.RoutingUpdatedAtUtc),
        ParseOptional(row.ClaimId),
        FromUnix(row.ClaimLeaseExpiresAtUtc),
        ReadModelPin(row), ParseOptional(row.ExecutionSessionId), ParseOptional(row.AcceptedAgentRunId), ParseOptional(row.LiveSessionId), FromUnix(row.LiveEvaluationCompletedAtUtc),
        new((AutomationExecutionTargetKind)row.ExecutionTargetKind, ParseOptional(row.TargetSessionId)), new(ParseOptional(row.ReportToSessionId)));

    private static ExecutionModelPin? ReadModelPin(TriggerOccurrenceRecord row)
    {
        if (string.IsNullOrWhiteSpace(row.ModelCatalogKey)
            || string.IsNullOrWhiteSpace(row.ModelProviderAlias)
            || string.IsNullOrWhiteSpace(row.ModelId)
            || row.ModelSource is not int source
            || !Enum.IsDefined(typeof(ExecutionModelSource), source))
        {
            return null;
        }

        return new ExecutionModelPin(
            row.ModelCatalogKey,
            row.ModelProviderAlias,
            row.ModelId,
            row.ModelReasoningEffort,
            (ExecutionModelSource)source);
    }

    private static DateTimeOffset? FromUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static Guid? ParseOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
}
