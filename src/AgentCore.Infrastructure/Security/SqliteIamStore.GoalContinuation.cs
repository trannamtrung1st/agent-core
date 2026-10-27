using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantGoalContinuationStore
{
    private string goalContinuationCursor = "";
    public async ValueTask<IReadOnlyList<GoalContinuationCandidate>> ListGoalContinuationCandidatesAsync(int limit, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var result = new List<GoalContinuationCandidate>(); var at = (clock ?? TimeProvider.System).GetUtcNow(); var after = goalContinuationCursor;
        var rows = await db.Set<SessionGoalRow>().FromSqlInterpolated($"""
            SELECT g.* FROM SessionGoals g JOIN Sessions s ON s.SessionId=g.SessionId JOIN SessionSnapshots snap ON snap.SessionId=s.SessionId
            WHERE lower(g.TenantId||'|'||g.GoalId)>{after} AND json_extract(g.Json,'$.Status')=1 AND json_extract(g.Json,'$.Plan.AutoContinuation.Enabled')=1
              AND json_extract(g.Json,'$.AutoRunsUsed')<json_extract(g.Json,'$.Plan.AutoContinuation.MaxRuns')
              AND julianday(json_extract(g.Json,'$.Plan.AutoContinuation.Until'))>julianday({at.ToString("O")})
              AND (json_extract(g.Json,'$.Plan.DeadlineAt') IS NULL OR julianday(json_extract(g.Json,'$.Plan.DeadlineAt'))>julianday({at.ToString("O")}))
              AND lower(s.FocusedGoalId)=lower(g.GoalId) AND snap.LifecycleStatus='Active' AND s.ArchivedAtUtc IS NULL
              AND s.DurablyDeletedAtUtc IS NULL AND (s.RetentionDueAtUtc IS NULL OR s.RetentionDueAtUtc>{at.ToUnixTimeMilliseconds()})
            ORDER BY g.TenantId,g.GoalId LIMIT {Math.Clamp(limit, 1, 8) * 4}
            """).AsNoTracking().Include(g => g.Checkpoints).ToArrayAsync(ct);
        goalContinuationCursor = rows.Length == 0 ? "" : (rows[^1].TenantId.ToString("D") + "|" + rows[^1].GoalId.ToString("D")).ToLowerInvariant();
        foreach (var row in rows)
        {
            var goal = GoalPersistence.Value(row); var root = await OwnedSessions(db, row.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == row.SessionId, ct); if (root is null) continue;
            var session = await SessionView(db, root, ct);
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == row.SessionId && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct)) continue;
            var sourceRow = await db.AgentRuns.FromSqlInterpolated($"""
                SELECT r.* FROM AgentRuns r WHERE r.TenantId={row.TenantId} AND r.SessionId={row.SessionId}
                AND json_extract(r.PayloadJson,'$.admission.goalWork.goalId')={goal.GoalId.ToString("D")}
                AND EXISTS (SELECT 1 FROM GoalCheckpoints c WHERE c.TenantId={row.TenantId} AND c.GoalId={goal.GoalId} AND json_extract(c.StateJson,'$.Status')=4 AND lower(json_extract(c.StateJson,'$.SourceRunId'))=lower(r.AgentRunId))
                AND NOT EXISTS (SELECT 1 FROM Activations a WHERE a.SessionId=r.SessionId AND a.DedupeKey='goal:'||replace(lower(r.AgentRunId),'-','')||':'||replace(lower({goal.GoalId.ToString("D")}),'-','')||':'||{goal.PlanRevision})
                ORDER BY r.CreatedAtUtc DESC,r.AgentRunId DESC LIMIT 1
                """).AsNoTracking().SingleOrDefaultAsync(ct);
            if (sourceRow is null) continue; var source = AgentRunStoreMapping.ToDomain(sourceRow);
            if (!GoalContinuationRules.CanContinue(goal, source, session.Snapshot, at)
                || await db.Activations.AnyAsync(a => a.SessionId == row.SessionId && a.DedupeKey == GoalContinuationRules.Key(source, goal), ct)) continue;
            result.Add(new(goal, source, session)); if (result.Count >= Math.Clamp(limit, 1, 8)) break;
        }
        return result;
    }
    public async ValueTask<bool> TryAdmitGoalContinuationAsync(GoalContinuationAdmission admission, Guid activityId, DateTimeOffset at, CancellationToken ct)
    {
        var run = admission.Run; var tenant = run.Owner.TenantId!.Value;
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var root = await OwnedSessions(db, tenant).SingleOrDefaultAsync(s => s.SessionId == run.SessionId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var session = await SessionView(db, root, ct);
        if (await db.Activations.AnyAsync(a => a.SessionId == root.SessionId && a.DedupeKey == run.Admission.Activation.DedupeKey, ct)) return false;
        var sourceRow = await OwnedRuns(db, tenant).SingleOrDefaultAsync(r => r.AgentRunId == admission.SourceRunId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var source = AgentRunStoreMapping.ToDomain(sourceRow);
        var row = await db.Set<SessionGoalRow>().Include(g => g.Checkpoints).SingleOrDefaultAsync(g => g.TenantId == tenant && g.GoalId == run.Admission.GoalWork!.GoalId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var goal = GoalPersistence.Value(row);
        var conflicting = await db.AgentRuns.AsNoTracking().Where(r => r.SessionId == root.SessionId && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed).Take(1).ToArrayAsync(ct);
        var laterInput = await db.Entries.AsNoTracking().Where(e => e.SessionId == root.SessionId && e.Role == nameof(ConversationRole.User) && e.EntrySequence > source.Admission.GoalWork!.InputSequence).Take(1).ToArrayAsync(ct);
        GoalContinuationRules.Admission(admission, goal, source, session, state.Tenant.Revision, conflicting.Select(AgentRunStoreMapping.ToDomain), laterInput.Select(SqliteMemoryStore.ToEntry), at);
        var agentRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct) ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(agentRow); await GoalContinuationService.RequireAsync(state, agent, source, new(this), ct);
        await RequireSpaceExecutionSource(db, run, state, new(this), ct, admission: true);
        if (run.Admission.Configuration?.InstanceRevision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
        var next = goal with { Revision = goal.Revision + 1, AutoRunsUsed = goal.AutoRunsUsed + 1, UpdatedAt = at,
                Checkpoints = goal.Checkpoints.Select(c => c.CheckpointId == run.Admission.GoalWork!.CheckpointId ? c with { Status = GoalCheckpointStatus.InProgress, SourceRunId = run.AgentRunId } : c).ToArray() };
        var mapped = GoalPersistence.Row(next); db.Entry(row).CurrentValues.SetValues(mapped);
        foreach (var child in row.Checkpoints) db.Entry(child).CurrentValues.SetValues(mapped.Checkpoints.Single(c => c.CheckpointId == child.CheckpointId));
        var activity = new GoalActivity(activityId, tenant, source.SessionId, goal.GoalId, agent.PrincipalId, run.Admission.Activation.DedupeKey,
            GoalRules.Hash(run.Admission.GoalWork), "AutoContinuation", next.Revision, goal.PlanRevision, at, run.Admission.GoalWork!.CheckpointId, run.AgentRunId);
        db.Add(GoalPersistence.Row(activity, next)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run)); db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(session.Snapshot, run));
        var tenantRow = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct); db.Entry(tenantRow).CurrentValues.SetValues(tenantRow with { Revision = tenantRow.Revision + 1 });
        db.Add(new AuthorizationAudit(activityId, tenant, agent.PrincipalId, "goals.continue", goal.GoalId.ToString("D"), "allowed", "goal_successor_admitted", at));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
}
