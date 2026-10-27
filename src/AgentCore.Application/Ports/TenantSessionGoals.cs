using AgentCore.Domain.Goals;

namespace AgentCore.Application.Ports;

public enum GoalCommandKind { Create, Plan, PlanDecision, Progress, Verify, Acceptance, Control }
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalCommand(GoalCommandKind Kind, string RequestKey, long ExpectedSecurityRevision, long ExpectedGoalRevision,
    long ExpectedPlanRevision, GoalPlan? Plan = null, Guid? CheckpointId = null, string? Note = null, string? Action = null,
    GoalEvidence? Evidence = null, bool Confirm = false);
public sealed record GoalActor(TenantSessionInitiator? Human = null, TenantRuntimeFrame? Agent = null, string? Tool = null)
{ public Guid PrincipalId => Human?.PrincipalId ?? Agent?.Owner.ExecutorPrincipalId ?? throw new ArgumentException("Goal actor is required."); }
public sealed record GoalMutation(Guid TenantId, Guid SessionId, Guid GoalId, GoalActor Actor, GoalCommand Command, Guid ActivityId, DateTimeOffset At);
public sealed record GoalPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record GoalMutationResult(SessionGoal Goal, long SecurityRevision, bool Replayed = false);
public sealed record GoalFocus(TenantSessionRoot Owner, TenantSessionInitiator Human, Guid GoalId, Guid RequestId,
    long ExpectedSecurityRevision, long ExpectedSessionRevision, long ExpectedGoalRevision);
public sealed record GoalFocusResult(Guid GoalId, long FocusRevision, long SessionRevision, long SecurityRevision);
public interface ITenantSessionGoalStore
{
    ValueTask<SessionGoal?> FindGoalAsync(Guid tenant, Guid session, Guid goal, CancellationToken ct = default);
    ValueTask<GoalPage<SessionGoal>> ListGoalsAsync(Guid tenant, Guid session, int page, int pageSize, CancellationToken ct = default);
    ValueTask<GoalPage<GoalActivity>> ListGoalActivityAsync(Guid tenant, Guid session, Guid goal, int page, int pageSize, CancellationToken ct = default);
    ValueTask<GoalMutationResult?> FindGoalRequestAsync(Guid tenant, Guid session, Guid actor, string requestKey, CancellationToken ct = default);
    ValueTask<GoalMutationResult> MutateGoalAsync(GoalMutation request, CancellationToken ct = default);
    ValueTask<GoalFocusResult> FocusGoalAsync(GoalFocus request, Guid auditId, CancellationToken ct = default);
}
public interface ITenantSessionGoalControl
{ ValueTask<GoalFocusResult> FocusGoalAsync(GoalFocus request, CancellationToken ct = default); }
public interface ITenantRuntimeGoalWriter
{
    ValueTask<GoalFocusResult> SaveGoalFocusAsync(AgentCore.Domain.Conversation.SessionSnapshot snapshot, long expectedRevision, GoalFocus request, CancellationToken ct);
}
public interface ITenantRuntimeGoalStore
{
    ValueTask<GoalFocusResult> SaveGoalFocusAsync(TenantRuntimeFrame frame, AgentCore.Domain.Conversation.SessionSnapshot snapshot, long expectedRevision, GoalFocus request, Guid auditId, CancellationToken ct);
}
public sealed record GoalContinuationCandidate(SessionGoal Goal, AgentCore.Domain.Conversation.AgentRun Source, TenantSession Session);
public sealed record GoalContinuationAdmission(AgentCore.Domain.Conversation.AgentRun Run, Guid SourceRunId, long ExpectedSecurityRevision, long ExpectedGoalRevision, long ExpectedSessionRevision);
public interface ITenantGoalContinuationStore
{
    ValueTask<IReadOnlyList<GoalContinuationCandidate>> ListGoalContinuationCandidatesAsync(int limit, CancellationToken ct);
    ValueTask<bool> TryAdmitGoalContinuationAsync(GoalContinuationAdmission admission, Guid activityId, DateTimeOffset at, CancellationToken ct);
}
