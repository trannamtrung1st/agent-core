using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantRuntimeGoalStore
{
    public ValueTask<GoalFocusResult> FocusGoalAsync(GoalFocus request, Guid auditId, CancellationToken ct = default) => FocusGoal(request, auditId, null, null, 0, ct);
    public ValueTask<GoalFocusResult> SaveGoalFocusAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, GoalFocus request, Guid auditId, CancellationToken ct) => FocusGoal(request, auditId, frame, snapshot, expectedRevision, ct);
    private async ValueTask<GoalFocusResult> FocusGoal(GoalFocus request, Guid auditId, TenantRuntimeFrame? frame, SessionSnapshot? snapshot, long expectedRevision, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, request.Owner.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var root = await OwnedSessions(db, request.Owner.TenantId).SingleOrDefaultAsync(s => s.SessionId == request.Owner.SessionId.ToString("D"), ct) ?? throw new AccessDeniedException("resource_unavailable");
        var saved = await SessionView(db, root, ct);
        var goalRow = await db.Set<SessionGoalRow>().Include(g => g.Checkpoints).SingleOrDefaultAsync(g => g.TenantId == root.TenantId && g.GoalId == request.GoalId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var goal = GoalPersistence.Value(goalRow); var at = (clock ?? TimeProvider.System).GetUtcNow();
        await RequireGoalActor(db, new(root.TenantId, request.Owner.SessionId, goal.GoalId, new(request.Human), new(GoalCommandKind.Control, request.RequestId.ToString("D"), state.Tenant.Revision, goal.Revision, goal.PlanRevision), auditId, at), state, saved, goal, "goals.manage", true, ct);
        if (GoalFocusRules.Replay(request, saved.Snapshot)) return new(goal.GoalId, saved.Snapshot.GoalFocusRevision, saved.Snapshot.Revision, state.Tenant.Revision);
        var next = GoalFocusRules.Apply(request, goal, saved, state.Tenant.Revision, at, frame is not null);
        if (frame is null)
        {
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status == (int)AgentRunStatus.Running, ct)) throw new SecurityConflictException("session_in_use");
            var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct); SqliteMemoryStore.ApplySession(row, next);
        }
        else
        {
            if (snapshot is null || snapshot.FocusedGoalId != next.FocusedGoalId || snapshot.GoalFocusRevision != next.GoalFocusRevision || snapshot.GoalFocusRequestId != next.GoalFocusRequestId) throw new SecurityConflictException("stale_goal_focus");
            next = snapshot; await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, next, expectedRevision, ct, goalFocus: true);
        }
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == root.TenantId, ct); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = tenant.Revision + 1 });
        db.Add(new AuthorizationAudit(auditId, root.TenantId, request.Human.PrincipalId, "goals.focus", goal.GoalId.ToString("D"), "allowed", "goal_focus_changed", at));
        await RequireHumanSessionAsync(db, root.TenantId, request.Human, ct); await CommitReceiptAsync(db, tx, ct);
        return new(goal.GoalId, next.GoalFocusRevision, next.Revision, state.Tenant.Revision + 1);
    }
}
