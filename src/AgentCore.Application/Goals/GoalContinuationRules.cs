using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
namespace AgentCore.Application.Goals;
public static class GoalContinuationRules
{
    public static bool CanContinue(SessionGoal goal, AgentRun source, SessionSnapshot session, DateTimeOffset now)
    {
        if (source.Admission.GoalWork is not { } b || source.Status != AgentRunStatus.Completed || source.CancellationRequested || source.RequiresEffectReconciliation || source.Result?.AttentionRequired == true || source.Result?.OutcomeKind == AgentRunOutcomeKind.NeedsAttention
            || goal.Status != GoalStatus.Active || goal.PendingPlan is not null || goal.Plan.AutoContinuation is not { Enabled: true, Until: { } until } auto
            || until <= now || goal.AutoRunsUsed >= auto.MaxRuns || goal.AutoRunsUsed >= GoalRules.MaxAutoRuns
            || goal.Plan.DeadlineAt is { } deadline && deadline <= now || session.FocusedGoalId != goal.GoalId || session.GoalFocusRevision != b.FocusRevision
            || session.ContextSelection?.SpaceId != goal.SpaceId || session.LifecycleStatus != SessionLifecycleStatus.Active || session.ArchivedAt is not null
            || session.DurablyDeletedAt is not null || SessionRetention.IsDue(session, now) || session.PendingAgentInputIds.Count > 0
            || source.SessionId != goal.SessionId || source.Owner.TenantId != goal.TenantId || b.GoalId != goal.GoalId || b.PlanRevision != goal.PlanRevision
            || goal.Checkpoints.SingleOrDefault(c => c.CheckpointId == b.CheckpointId)?.Status != GoalCheckpointStatus.Verified
            || GoalRules.Next(goal) is not { } next || next.CheckpointId == b.CheckpointId || goal.Checkpoints.Single(c => c.CheckpointId == next.CheckpointId).Status != GoalCheckpointStatus.Pending) return false;
        return true;
    }
    public static bool CanDispatch(SessionGoal? goal, AgentRun run, SessionSnapshot session, bool laterUserInput, DateTimeOffset now)
    {
        if (run.Admission.GoalWork is not { } b) return true;
        return goal is not null && goal.Status == GoalStatus.Active && goal.PendingPlan is null && goal.PlanRevision == b.PlanRevision
            && goal.GoalId == b.GoalId && session.FocusedGoalId == b.GoalId && session.GoalFocusRevision == b.FocusRevision
            && session.ContextSelection?.SpaceId == b.SpaceId && goal.SpaceId == b.SpaceId && !laterUserInput
            && session.LifecycleStatus == SessionLifecycleStatus.Active && session.ArchivedAt is null && !SessionRetention.IsDue(session, now)
            && (goal.Plan.DeadlineAt is null || goal.Plan.DeadlineAt > now)
            && (run.Admission.Activation.Kind != ActivationKind.GoalContinuation || goal.Plan.AutoContinuation is { Enabled: true, Until: { } until } auto
                && until > now && goal.AutoRunsUsed <= auto.MaxRuns && goal.AutoRunsUsed <= GoalRules.MaxAutoRuns);
    }
    public static string Key(AgentRun source, SessionGoal goal) => $"goal:{source.AgentRunId:N}:{goal.GoalId:N}:{goal.PlanRevision}";
    public static void Admission(GoalContinuationAdmission a, SessionGoal goal, AgentRun source, TenantSession saved, long securityRevision, IEnumerable<AgentRun> siblings, IEnumerable<ConversationEntry> entries, DateTimeOffset now)
    {
        if (!CanContinue(goal, source, saved.Snapshot, now) || a.ExpectedGoalRevision != goal.Revision || a.ExpectedSecurityRevision != securityRevision
            || a.ExpectedSessionRevision != saved.Snapshot.Revision || a.Run.SessionId != source.SessionId || a.Run.Owner != source.Owner
            || a.Run.Admission.Activation.Kind != ActivationKind.GoalContinuation || a.Run.Admission.Activation.DedupeKey != Key(source, goal)
            || a.Run.Admission.Activation.SourceAgentRunId != source.AgentRunId || a.Run.Admission.Activation.SourceSessionId != source.SessionId
            || a.Run.Admission.Activation.ParentActivationId != source.ActivationId || a.Run.Admission.Activation.RootActivationId != source.Admission.Activation.RootActivationId
            || a.Run.Admission.ExecutionContext?.Origin != ExecutionOrigin.GoalContinuation || a.Run.Admission.ExecutionContext.ParentRunId != source.AgentRunId
            || a.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
            || a.Run.Admission.GoalWork is not { } binding || binding.InputSequence != source.Admission.GoalWork!.InputSequence
            || siblings.Any(r => !r.IsTerminal || r.Admission.Activation.Kind == ActivationKind.GoalContinuation && r.Admission.Activation.SourceAgentRunId == source.AgentRunId)
            || entries.Any(e => e.Role == ConversationRole.User && e.Sequence > source.Admission.GoalWork!.InputSequence)) throw new SecurityConflictException("goal_continuation_ineligible");
        GoalAuthority.Binding(goal, saved.Snapshot, binding);
    }
}
