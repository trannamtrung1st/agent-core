using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;

namespace AgentCore.Application.Goals;

public static class GoalFocusRules
{
    public static bool Replay(GoalFocus request, SessionSnapshot snapshot) => snapshot.GoalFocusRequestId == request.RequestId && snapshot.FocusedGoalId == request.GoalId;
    public static SessionSnapshot Apply(GoalFocus request, SessionGoal goal, TenantSession saved, long securityRevision, DateTimeOffset at, bool live)
    {
        if (request.Owner != saved.Owner || request.RequestId == Guid.Empty || request.GoalId != goal.GoalId || goal.SessionId != saved.Owner.SessionId
            || goal.TenantId != saved.Owner.TenantId || goal.SpaceId != saved.Snapshot.ContextSelection?.SpaceId) throw new AccessDeniedException("goal_space_mismatch");
        if (request.ExpectedSecurityRevision != securityRevision || request.ExpectedGoalRevision != goal.Revision
            || (!live && request.ExpectedSessionRevision != saved.Snapshot.Revision) || live && request.ExpectedSessionRevision > saved.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        TenantHumanInputRules.Eligible(saved.Snapshot);
        return saved.Snapshot with { FocusedGoalId = goal.GoalId, GoalFocusRevision = saved.Snapshot.GoalFocusRevision + 1,
            GoalFocusRequestId = request.RequestId, Revision = saved.Snapshot.Revision + 1, UpdatedAt = at };
    }
}
