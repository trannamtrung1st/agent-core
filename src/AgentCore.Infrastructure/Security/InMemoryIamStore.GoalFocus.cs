using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantRuntimeGoalStore
{
    public ValueTask<GoalFocusResult> FocusGoalAsync(GoalFocus request, Guid auditId, CancellationToken ct = default) => FocusGoal(request, auditId, null, null, 0, ct);
    public ValueTask<GoalFocusResult> SaveGoalFocusAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, GoalFocus request, Guid auditId, CancellationToken ct) => FocusGoal(request, auditId, frame, snapshot, expectedRevision, ct);
    private ValueTask<GoalFocusResult> FocusGoal(GoalFocus request, Guid auditId, TenantRuntimeFrame? frame, SessionSnapshot? snapshot, long expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var state = _tenants[request.Owner.TenantId];
            var saved = sessionBacking.ReadTenantSession(request.Owner.TenantId, request.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var goal = goals.GetValueOrDefault((request.Owner.TenantId, request.GoalId)) ?? throw new AccessDeniedException("resource_unavailable");
            RequireGoalActor(new(request.Owner.TenantId, request.Owner.SessionId, request.GoalId, new(request.Human), new(GoalCommandKind.Control, request.RequestId.ToString("D"), state.Tenant.Revision, goal.Revision, goal.PlanRevision), auditId, (clock ?? TimeProvider.System).GetUtcNow()), state, saved, goal, "goals.manage", true, ct);
            if (GoalFocusRules.Replay(request, saved.Snapshot)) return ValueTask.FromResult(new GoalFocusResult(goal.GoalId, saved.Snapshot.GoalFocusRevision, saved.Snapshot.Revision, state.Tenant.Revision));
            var at = (clock ?? TimeProvider.System).GetUtcNow(); var next = GoalFocusRules.Apply(request, goal, saved, state.Tenant.Revision, at, frame is not null);
            if (frame is null) sessionBacking.SaveQuiescentGoalFocus(next, saved.Snapshot.Revision);
            else
            {
                if (snapshot is null || snapshot.FocusedGoalId != next.FocusedGoalId || snapshot.GoalFocusRevision != next.GoalFocusRevision || snapshot.GoalFocusRequestId != next.GoalFocusRequestId) throw new SecurityConflictException("stale_goal_focus");
                next = snapshot; SaveTenantRuntimeState(frame, next, expectedRevision, at, goalFocus: true);
            }
            var revision = checked(state.Tenant.Revision + 1); _tenants[request.Owner.TenantId] = state with { Tenant = state.Tenant with { Revision = revision } };
            _audit.Add(new(auditId, request.Owner.TenantId, request.Human.PrincipalId, "goals.focus", goal.GoalId.ToString("D"), "allowed", "goal_focus_changed", at));
            return ValueTask.FromResult(new GoalFocusResult(goal.GoalId, next.GoalFocusRevision, next.Revision, revision));
        }
    }
}
