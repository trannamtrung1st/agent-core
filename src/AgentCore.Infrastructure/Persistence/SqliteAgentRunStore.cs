using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAgentRunStore(IDbContextFactory<AgentCoreDbContext> contexts,
    SqliteMemoryStore sessions, IDiagnosticIdSource diagnostics) : IAgentRunStore
{
    public async ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default)
    {
        AgentRunStoreMapping.ValidateAdmission(snapshot, run);
        var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, run);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existingAdmission = await db.Activations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.SessionId == activation.SessionId && row.DedupeKey == activation.DedupeKey
            || activation.BackgroundSourceKey != null && row.BackgroundSourceKey == activation.BackgroundSourceKey
                && row.AgentInstanceId == activation.AgentInstanceId && row.ProfileId == activation.ProfileId,
            cancellationToken).ConfigureAwait(false);
        if (existingAdmission is not null)
        {
            if (existingAdmission.AdmissionHash != activation.AdmissionHash
                || existingAdmission.AgentInstanceId != activation.AgentInstanceId || existingAdmission.ProfileId != activation.ProfileId)
                throw AgentCoreErrors.Conflict("Admission key belongs to different input or execution pins.");
            var existing = await db.AgentRuns.AsNoTracking().SingleAsync(row => row.ActivationId == existingAdmission.ActivationId,
                cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentRunAdmissionResult(false, AgentRunStoreMapping.ToDomain(existing));
        }
        if (run.Admission.Activation.Kind == ActivationKind.ImmediateBackground)
        {
            var sourceId = run.Admission.Activation.SourceAgentRunId!.Value.ToString("D");
            var source = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == sourceId,
                cancellationToken).ConfigureAwait(false);
            var sourceSession = source is null ? null : await db.Sessions.AsNoTracking().Include(row => row.Snapshot)
                .SingleOrDefaultAsync(row => row.SessionId == source.SessionId, cancellationToken).ConfigureAwait(false);
            AgentRunStoreMapping.ValidateImmediateSource(run, source is null ? null : AgentRunStoreMapping.ToDomain(source),
                sourceSession is not null && sourceSession.DurablyDeletedAtUtc is null && sourceSession.ArchivedAtUtc is null
                && sourceSession.Status is not (nameof(SessionStatus.Ended) or nameof(SessionStatus.Ending))
                && sourceSession.Snapshot?.LifecycleStatus is not (nameof(SessionLifecycleStatus.Completed)
                    or nameof(SessionLifecycleStatus.Cancelled) or nameof(SessionLifecycleStatus.Expired) or nameof(SessionLifecycleStatus.Ended))
                && sourceSession.AgentInstanceId == run.AgentInstanceId.ToString("D")
                && sourceSession.Snapshot?.ProfileId == run.ProfileId.ToString("D"));
        }
        await sessions.StageSaveAsync(db, snapshot, expectedSessionRevision, cancellationToken).ConfigureAwait(false);
        db.Activations.Add(activation);
        db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
        db.ActivationSourceEntries.AddRange(run.Admission.Activation.SourceEntryIds.Select((id, ordinal) =>
            new ActivationSourceEntryRecord { SessionId = run.SessionId.ToString("D"), EntryId = id.ToString("D"),
                ActivationId = run.ActivationId.ToString("D"), Ordinal = ordinal }));
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AgentRunAdmissionResult(true, run);
    }

    public async ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid agentRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == agentRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }

    public async ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid activationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Activations.AsNoTracking().SingleOrDefaultAsync(row => row.ActivationId == activationId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : JsonSerializer.Deserialize<Activation>(row.PayloadJson, AgentRunStoreMapping.Json);
    }

    public async ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentRuns.AsNoTracking().Where(row => row.SessionId == sessionId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"))
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.AgentRunId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).ToArray();
    }

    public async ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        if (asOfUtc.Offset != TimeSpan.Zero || limit is < 1 or > AgentRunLimits.MaxListLimit)
            throw AgentCoreErrors.Validation("Runnable query requires UTC time and a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var utc = asOfUtc.ToUnixTimeMilliseconds();
        var rows = await db.AgentRuns.AsNoTracking().Where(row => row.Status == (int)AgentRunStatus.Queued
            || row.Status == (int)AgentRunStatus.WaitingToRetry && row.NextRetryAtUtc <= utc
            || row.Status == (int)AgentRunStatus.Running && row.LeaseExpiresAtUtc <= utc
            || row.Status == (int)AgentRunStatus.WaitingForApproval && row.ApprovalExpiresAtUtc <= utc)
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.AgentRunId).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).ToArray();
    }

    public async ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRuns.SingleOrDefaultAsync(row => row.AgentRunId == agentRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        if (row is null) throw AgentCoreErrors.NotFound("AgentRun was not found.");
        var run = AgentRunStoreMapping.ToDomain(row);
        if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
        {
            var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(entry => entry.SessionId == row.SessionId
                && entry.EntryId == entryId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.Role != nameof(ConversationRole.Assistant) || entry.Status != nameof(EntryStatus.Completed)
                || entry.ResponseId != run.ResponseId?.ToString("D"))
                throw AgentCoreErrors.Conflict("Completed outcome must reference the durable Session response.");
        }
        AgentRun updated;
        try { updated = command.Apply(run, diagnostics.NewId); }
        catch (Exception e) when (e is AgentRunTransitionException or ArgumentException)
        { throw AgentRunStoreMapping.Map(e); }
        AgentRunStoreMapping.Apply(row, updated);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task SaveAsync(AgentCoreDbContext db, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("AgentRun revision is stale."); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
        { throw AgentCoreErrors.Conflict("AgentRun admission or transition conflicts with existing state."); }
    }
}
