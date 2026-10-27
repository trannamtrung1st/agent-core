using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantSessionGoalStore
{
    public async ValueTask<SessionGoal?> FindGoalAsync(Guid tenant, Guid session, Guid goal, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == session.ToString("D"), ct)) return null;
        var row = await db.Set<SessionGoalRow>().AsNoTracking().Include(g => g.Checkpoints).SingleOrDefaultAsync(g => g.TenantId == tenant && g.SessionId == session.ToString("D") && g.GoalId == goal, ct);
        return row is null ? null : GoalPersistence.Value(row);
    }
    public async ValueTask<GoalPage<SessionGoal>> ListGoalsAsync(Guid tenant, Guid session, int page, int pageSize, CancellationToken ct = default)
    {
        GoalBounds(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == session.ToString("D"), ct)) return new([], 0, page, pageSize);
        var query = db.Set<SessionGoalRow>().AsNoTracking().Where(g => g.TenantId == tenant && g.SessionId == session.ToString("D"));
        var total = await query.CountAsync(ct); var rows = await query.Include(g => g.Checkpoints).OrderBy(g => g.GoalId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(GoalPersistence.Value).ToArray(), total, page, pageSize);
    }
    public async ValueTask<GoalPage<GoalActivity>> ListGoalActivityAsync(Guid tenant, Guid session, Guid goal, int page, int pageSize, CancellationToken ct = default)
    {
        GoalBounds(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == session.ToString("D"), ct)) return new([], 0, page, pageSize);
        var query = db.Set<GoalActivityRow>().AsNoTracking().Where(g => g.TenantId == tenant && g.SessionId == session.ToString("D") && g.GoalId == goal);
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(g => g.Revision).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(g => JsonSerializer.Deserialize<GoalActivity>(g.Json)!).ToArray(), total, page, pageSize);
    }
    public async ValueTask<GoalMutationResult?> FindGoalRequestAsync(Guid tenant, Guid session, Guid actor, string requestKey, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == session.ToString("D"), ct)) return null;
        var row = await db.Set<GoalActivityRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.SessionId == session.ToString("D") && a.ActorId == actor && a.RequestKey == requestKey, ct);
        var state = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant, ct);
        return row is null || state is null ? null : new(JsonSerializer.Deserialize<SessionGoal>(row.ReceiptJson)!, state.Revision, true);
    }
    public async ValueTask<GoalMutationResult> MutateGoalAsync(GoalMutation request, CancellationToken ct = default)
    {
        request = request with { Command = GoalRules.Freeze(request.Command) };
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var root = await OwnedSessions(db, request.TenantId).SingleOrDefaultAsync(s => s.SessionId == request.SessionId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var session = await SessionView(db, root, ct);
        var row = await db.Set<SessionGoalRow>().Include(g => g.Checkpoints).SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.GoalId == request.GoalId, ct);
        var before = row is null ? null : GoalPersistence.Value(row);
        await RequireGoalActor(db, request, state, session, before, GoalTransitions.Permission(request.Command), true, ct);
        if (request.Command.Kind == GoalCommandKind.Create && request.Command.Confirm)
            await GoalAuthority.Require(state, request.Actor.PrincipalId, "goals.manage", TenantSessionRules.Scope(request.TenantId, request.SessionId), new(this), ct);
        var replay = await db.Set<GoalActivityRow>().SingleOrDefaultAsync(a => a.TenantId == request.TenantId && a.SessionId == root.SessionId && a.ActorId == request.Actor.PrincipalId && a.RequestKey == request.Command.RequestKey, ct);
        if (replay is not null)
        {
            if (replay.Fingerprint != GoalTransitions.Fingerprint(request)) throw new SecurityConflictException("goal_request_conflict");
            return new(JsonSerializer.Deserialize<SessionGoal>(replay.ReceiptJson)!, state.Tenant.Revision, true);
        }
        if (state.Tenant.Revision != request.Command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        if (before is null && await db.Set<SessionGoalRow>().CountAsync(g => g.TenantId == request.TenantId && g.SessionId == root.SessionId, ct) >= GoalRules.MaxGoals) throw new ArgumentException("Session Goal limit reached.");
        var (next, activity) = await GoalTransitions.ApplyAsync(request, before, session.Snapshot.ContextSelection!.SpaceId,
            (goal, checkpoint, evidence) => QualifyGoalEvidence(db, state, request.Actor, goal, checkpoint, evidence, ct));
        var mapped = GoalPersistence.Row(next);
        if (row is null) db.Add(mapped);
        else
        {
            db.Entry(row).CurrentValues.SetValues(mapped);
            // Replace this bounded child projection atomically. Domain rules preserve every verified
            // criterion and evidence fact; clearing first permits ordinal changes without transient unique-index collisions.
            db.RemoveRange(row.Checkpoints);
            await db.SaveChangesAsync(ct);
            foreach (var child in mapped.Checkpoints) db.Add(child);
        }
        db.Add(GoalPersistence.Row(activity, next));
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == request.TenantId, ct);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(activity.ActivityId, next.TenantId, request.Actor.PrincipalId, "goals." + request.Command.Kind.ToString().ToLowerInvariant(), next.GoalId.ToString("D"), "allowed", "goal_changed", request.At));
        if (request.Actor.Human is { } human) await RequireHumanSessionAsync(db, request.TenantId, human, ct);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new SecurityConflictException("stale_revision"); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw new SecurityConflictException("goal_write_conflict"); }
        return new(next, state.Tenant.Revision + 1);
    }
    private async Task RequireGoalActor(AgentCoreDbContext db, GoalMutation request, TenantSecurityState state, TenantSession session, SessionGoal? goal, string permission, bool mutate, CancellationToken ct)
    {
        if (request.Actor.Human is { } human) await RequireHumanSessionAsync(db, request.TenantId, human, ct);
        var spaceId = goal?.SpaceId ?? session.Snapshot.ContextSelection!.SpaceId;
        var agentRow = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == session.Owner.AgentInstanceId.ToString("D"), ct);
        var agent = agentRow is null ? null : Owned(agentRow);
        var runRow = request.Actor.Agent is { } frame ? await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == frame.RunId.ToString("D"), ct) : null;
        if (request.Actor.Agent is not null && request.Command.Kind is GoalCommandKind.Progress or GoalCommandKind.Verify
            && (runRow is null || AgentRunStoreMapping.ToDomain(runRow).Admission.GoalWork?.CheckpointId != request.Command.CheckpointId)) throw new AccessDeniedException("goal_checkpoint_binding_required");
        var space = await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == request.TenantId && s.SpaceId == spaceId, ct);
        var member = await db.Set<SpaceMembershipRecord>().SingleOrDefaultAsync(m => m.TenantId == request.TenantId && m.SpaceId == spaceId && m.InstanceId == session.Owner.AgentInstanceId.ToString("D"), ct);
        await GoalAuthority.RequireAsync(state, session, goal, request.Actor, permission, new(this), space?.Domain(), member?.Domain([], []), agent,
            runRow is null ? null : AgentRunStoreMapping.ToDomain(runRow), agent is null ? null : InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides),
            (clock ?? TimeProvider.System).GetUtcNow(), mutate, ct);
    }
    private async ValueTask<bool> QualifyGoalEvidence(AgentCoreDbContext db, TenantSecurityState state, GoalActor actor, SessionGoal goal, GoalCheckpointPlan checkpoint, GoalEvidence evidence, CancellationToken ct)
    {
        var runRow = evidence.RunId is { } id ? await OwnedRuns(db, goal.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), ct) : null;
        var attemptRow = await db.Set<EvaluationAttemptRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == goal.TenantId && a.AttemptId == evidence.SourceId, ct);
        var attempt = attemptRow is null ? null : EvaluationPersistence.Value(attemptRow);
        var artifact = await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == goal.TenantId && a.SessionId == goal.SessionId.ToString("D") && a.ArtifactId == evidence.SourceId.ToString("D"), ct);
        var artifactValue = artifact is null ? null : new ArtifactRecord(Guid.Parse(artifact.ArtifactId), Guid.Parse(artifact.SessionId), artifact.DisplayName, artifact.ContentType, artifact.ByteSize, artifact.Sha256Hex,
            null, null, DateTimeOffset.FromUnixTimeMilliseconds(artifact.CreatedAtUtc), artifact.AgentRunId is null ? null : Guid.Parse(artifact.AgentRunId), artifact.TenantId, artifact.CreatedByPrincipalId);
        return await GoalAuthority.EvidenceAsync(state, actor, goal, checkpoint, evidence, runRow is null ? null : AgentRunStoreMapping.ToDomain(runRow), attempt,
            attempt is null ? null : await Suite(db, goal.TenantId, attempt.SuiteId, attempt.SuiteVersion, ct), artifactValue, new(this), ct);
    }
    private static void GoalBounds(int page, int size) { if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
    private static async Task RetireGoals(AgentCoreDbContext db, Guid tenant, string session, CancellationToken ct)
    {
        var retained = db.Sessions.Local.SingleOrDefault(s => s.SessionId == session) ?? await db.Sessions.SingleOrDefaultAsync(s => s.SessionId == session, ct);
        if (retained?.DurablyDeletedAtUtc is null) return;
        // Flush the incumbent Session tombstone in this same transaction before cascading private activity.
        await db.SaveChangesAsync(ct);
        await db.Set<SessionGoalRow>().Where(g => g.TenantId == tenant && g.SessionId == session).ExecuteDeleteAsync(ct);
    }
}
