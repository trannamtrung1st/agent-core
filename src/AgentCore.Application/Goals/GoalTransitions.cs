using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Goals;

namespace AgentCore.Application.Goals;

// The only Goal transition owner; HTTP and tools supply commands, never replacement aggregates.
public static class GoalTransitions
{
    public static string Permission(GoalCommand command) => command.Kind is GoalCommandKind.PlanDecision or GoalCommandKind.Acceptance
        || command.Kind == GoalCommandKind.Create && command.Confirm ? "goals.accept" : "goals.manage";
    public static string Fingerprint(GoalMutation request) => GoalRules.Hash(new { request.GoalId, request.SessionId,
        Command = request.Command with { ExpectedSecurityRevision = 0 } });
    public static async ValueTask<(SessionGoal Goal, GoalActivity Activity)> ApplyAsync(GoalMutation r, SessionGoal? before, Guid space,
        Func<SessionGoal, GoalCheckpointPlan, GoalEvidence, ValueTask<bool>> evidence)
    {
        var c = r.Command;
        if (r.Actor.Agent is not null && (c.Confirm || c.Kind is GoalCommandKind.Acceptance or GoalCommandKind.PlanDecision or GoalCommandKind.Control)) throw new AccessDeniedException("human_acceptance_required");
        if (c.Kind == GoalCommandKind.Progress && c.Evidence is not null) throw new ArgumentException("Report progress without unverified source attribution; attach exact proof through verification.");
        if (!Enum.IsDefined(c.Kind) || string.IsNullOrWhiteSpace(c.RequestKey) || c.RequestKey.Length > 128 || c.RequestKey.Any(char.IsControl)
            || r.GoalId == Guid.Empty || r.ActivityId == Guid.Empty || c.Note?.Length > 2000) throw new ArgumentException("Invalid Goal command.");
        var human = r.Actor.Human is not null;
        if ((before?.Revision ?? 0) != c.ExpectedGoalRevision || (before?.PlanRevision ?? 0) != c.ExpectedPlanRevision) throw new SecurityConflictException("stale_goal_revision");
        if (before is not null && (before.TenantId != r.TenantId || before.SessionId != r.SessionId || before.GoalId != r.GoalId || before.SpaceId != space)) throw new AccessDeniedException("goal_space_mismatch");
        SessionGoal next;
        if (c.Kind == GoalCommandKind.Create)
        {
            if (before is not null || c.Plan is null || !human && c.Confirm) throw new ArgumentException("New Goals require an explicit plan; agents can only propose.");
            var plan = GoalRules.Validate(c.Plan, r.At);
            if (!human && plan.AutoContinuation!.Enabled) throw new AccessDeniedException("human_acceptance_required");
            next = new(r.GoalId, r.TenantId, r.SessionId, space, r.Actor.PrincipalId, r.At, r.At, 1, 1,
                c.Confirm ? GoalStatus.Active : GoalStatus.Proposed, plan, plan.Checkpoints.Select(p => new GoalCheckpoint(p.CheckpointId)).ToArray());
        }
        else
        {
            next = before ?? throw new AccessDeniedException("resource_unavailable");
            if (next.Status is GoalStatus.Completed or GoalStatus.Cancelled) throw new SecurityConflictException("goal_terminal");
            if (c.Kind == GoalCommandKind.Plan)
            {
                if (c.Plan is null) throw new ArgumentException("A revised plan is required.");
                var plan = GoalRules.Validate(c.Plan, r.At); GoalRules.PreserveVerified(next, plan);
                next = next with { PendingPlan = plan };
            }
            else if (c.Kind == GoalCommandKind.PlanDecision)
            {
                if (!human) throw new AccessDeniedException("human_acceptance_required");
                if (c.Action == "reject") next = next with { PendingPlan = null };
                else if (c.Action == "accept")
                {
                    var plan = GoalRules.Validate(next.PendingPlan ?? next.Plan, r.At); GoalRules.PreserveVerified(next, plan);
                    next = next with { Plan = plan, PendingPlan = null, PlanRevision = next.PendingPlan is null ? next.PlanRevision : next.PlanRevision + 1,
                        Status = next.Status == GoalStatus.Proposed ? GoalStatus.Active : next.Status,
                        Checkpoints = plan.Checkpoints.Select(p => next.Checkpoints.SingleOrDefault(x => x.CheckpointId == p.CheckpointId) is { Status: GoalCheckpointStatus.Verified } verified
                            ? verified : new GoalCheckpoint(p.CheckpointId)).ToArray(), FinalAcceptedAt = null, FinalAcceptedBy = null };
                }
                else throw new ArgumentException("Choose accept or reject.");
            }
            else if (c.Kind == GoalCommandKind.Control)
            {
                next = next with { Status = c.Action switch { "pause" when next.Status == GoalStatus.Active => GoalStatus.Paused,
                    "resume" when next.Status == GoalStatus.Paused => GoalStatus.Active, "cancel" => GoalStatus.Cancelled,
                    _ => throw new ArgumentException("Choose an eligible pause, resume or cancel action.") } };
            }
            else
            {
                if (next.Status != GoalStatus.Active || next.PendingPlan is not null) throw new SecurityConflictException("goal_review_or_resume_required");
                if (c.Kind == GoalCommandKind.Acceptance && c.CheckpointId is null)
                {
                    if (!human || !next.Plan.RequireFinalAcceptance || !GoalRules.Verified(next)) throw new SecurityConflictException("goal_not_ready_for_acceptance");
                    GoalRules.Text(c.Note, 2000);
                    if (c.Action != "accept") throw new ArgumentException("Final acceptance must explicitly accept the current criteria.");
                    next = next with { FinalAcceptedBy = r.Actor.PrincipalId, FinalAcceptedAt = r.At };
                }
                else
                {
                    var plan = next.Plan.Checkpoints.SingleOrDefault(p => p.CheckpointId == c.CheckpointId) ?? throw new ArgumentException("Checkpoint not found.");
                    var checkpoint = next.Checkpoints.Single(p => p.CheckpointId == c.CheckpointId);
                    if (checkpoint.Status == GoalCheckpointStatus.Verified && c.Kind != GoalCommandKind.Verify || !GoalRules.DependenciesVerified(next, checkpoint.CheckpointId)) throw new SecurityConflictException("checkpoint_not_eligible");
                    if (c.Kind == GoalCommandKind.Progress)
                    {
                        GoalRules.Text(c.Note, 2000);
                        checkpoint = checkpoint with { Note = c.Note, SourceRunId = r.Actor.Agent?.RunId ?? checkpoint.SourceRunId,
                            Status = c.Action switch { "blocked" => GoalCheckpointStatus.Blocked, "skip" when !plan.Required => GoalCheckpointStatus.Skipped,
                                null or "report" => GoalCheckpointStatus.InProgress, _ => throw new ArgumentException("Required checkpoints cannot be skipped.") } };
                    }
                    else if (c.Kind is GoalCommandKind.Verify or GoalCommandKind.Acceptance)
                    {
                        GoalEvidence item;
                        if (c.Kind == GoalCommandKind.Acceptance)
                        {
                            if (!human || !plan.Verification.Any(v => v.Kind == GoalVerificationKind.HumanAcceptance)) throw new AccessDeniedException("human_acceptance_required");
                            GoalRules.Text(c.Note, 2000);
                            if (c.Action == "reject")
                            {
                                checkpoint = checkpoint with { Status = GoalCheckpointStatus.Blocked, Note = c.Note };
                                next = next with { Checkpoints = next.Checkpoints.Select(p => p.CheckpointId == checkpoint.CheckpointId ? checkpoint : p).ToArray() };
                                return Finish(r, before, next);
                            }
                            if (c.Action != "accept") throw new ArgumentException("Choose accept or reject.");
                            item = new(GoalVerificationKind.HumanAcceptance, r.ActivityId, PrincipalId: r.Actor.PrincipalId, AcceptedAt: r.At, Reason: c.Note);
                        }
                        else item = c.Evidence is { Kind: not GoalVerificationKind.HumanAcceptance } source ? source : throw new ArgumentException("An exact Evaluation or Artifact publication reference is required.");
                        if (!plan.Verification.Any(v => v.Kind == item.Kind)) throw new ArgumentException("This evidence does not match the saved verification policy.");
                        var qualified = await evidence(next, plan, item).ConfigureAwait(false);
                        var items = (checkpoint.Evidence ?? []).Where(e => e.Kind != item.Kind).Append(item).ToArray();
                        if (items.Length > GoalRules.MaxEvidence) throw new ArgumentException("Evidence limit reached.");
                        var all = qualified && plan.Verification.All(v => items.Any(e => e.Kind == v.Kind));
                        if (all) foreach (var existing in items) all &= await evidence(next, plan, existing).ConfigureAwait(false);
                        checkpoint = checkpoint with { Evidence = items, SourceRunId = item.RunId ?? checkpoint.SourceRunId,
                            Status = all ? GoalCheckpointStatus.Verified : GoalCheckpointStatus.VerificationPending,
                            Note = all ? "Verified against the accepted criteria." : "Evidence is missing, denied, pending, failed or inconclusive. Inspect its source before requesting verification again." };
                    }
                    else throw new ArgumentException("Invalid checkpoint action.");
                    next = next with { Checkpoints = next.Checkpoints.Select(p => p.CheckpointId == checkpoint.CheckpointId ? checkpoint : p).ToArray() };
                }
            }
        }
        if (next.Status == GoalStatus.Active && next.PendingPlan is null && GoalRules.Verified(next) && (!next.Plan.RequireFinalAcceptance || next.FinalAcceptedBy is not null))
        {
            // Requalify every required source at the single completion transition.
            var valid = true;
            foreach (var p in next.Plan.Checkpoints.Where(p => p.Required))
                foreach (var source in next.Checkpoints.Single(x => x.CheckpointId == p.CheckpointId).Evidence ?? [])
                    valid &= await evidence(next, p, source).ConfigureAwait(false);
            if (valid) next = next with { Status = GoalStatus.Completed };
        }
        return Finish(r, before, next);
    }
    private static (SessionGoal, GoalActivity) Finish(GoalMutation r, SessionGoal? before, SessionGoal next)
    {
        next = next with { Revision = (before?.Revision ?? 0) + 1, UpdatedAt = r.At };
        return (GoalRules.Freeze(next), new(r.ActivityId, r.TenantId, r.SessionId, r.GoalId, r.Actor.PrincipalId, r.Command.RequestKey, Fingerprint(r),
            r.Command.Kind.ToString(), next.Revision, next.PlanRevision, r.At, r.Command.CheckpointId, r.Actor.Agent?.RunId ?? r.Command.Evidence?.RunId,
            r.Command.Note, r.Command.Kind is GoalCommandKind.Plan or GoalCommandKind.PlanDecision ? before?.Plan : null,
            r.Command.Kind is GoalCommandKind.Create or GoalCommandKind.Plan or GoalCommandKind.PlanDecision ? next.PendingPlan ?? next.Plan : null, r.Command.Evidence));
    }
}
