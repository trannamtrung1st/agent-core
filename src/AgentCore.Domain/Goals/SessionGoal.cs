using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Evaluations;

namespace AgentCore.Domain.Goals;

public enum GoalStatus { Proposed, Active, Paused, Completed, Cancelled }
public enum GoalCheckpointStatus { Pending, InProgress, Blocked, VerificationPending, Verified, Skipped }
public enum GoalVerificationKind { EvaluationPass, SourceReceipt, HumanAcceptance }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalVerification(GoalVerificationKind Kind, Guid? SuiteId = null, int? SuiteVersion = null, string? SuiteHash = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalCheckpointPlan(Guid CheckpointId, string Title, string ExpectedOutcome, bool Required,
    IReadOnlyList<Guid> Dependencies, IReadOnlyList<GoalVerification> Verification);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalAutoContinuation(bool Enabled = false, int MaxRuns = 0, DateTimeOffset? Until = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalPlan(string Title, string Objective, string SuccessCriteria, IReadOnlyList<GoalCheckpointPlan> Checkpoints,
    DateTimeOffset? DeadlineAt = null, bool RequireFinalAcceptance = false, GoalAutoContinuation? AutoContinuation = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalEvidence(GoalVerificationKind Kind, Guid SourceId, Guid? RunId = null, Guid? PrincipalId = null,
    DateTimeOffset? AcceptedAt = null, string? Reason = null);
public sealed record GoalCheckpoint(Guid CheckpointId, GoalCheckpointStatus Status = GoalCheckpointStatus.Pending,
    string? Note = null, Guid? SourceRunId = null, IReadOnlyList<GoalEvidence>? Evidence = null);
public sealed record SessionGoal(Guid GoalId, Guid TenantId, Guid SessionId, Guid SpaceId, Guid CreatedByPrincipalId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision, long PlanRevision, GoalStatus Status, GoalPlan Plan,
    IReadOnlyList<GoalCheckpoint> Checkpoints, GoalPlan? PendingPlan = null, Guid? FinalAcceptedBy = null,
    DateTimeOffset? FinalAcceptedAt = null, int AutoRunsUsed = 0);
public sealed record GoalWorkContext(Guid GoalId, Guid CheckpointId, long PlanRevision, long GoalRevision, string PlanHash,
    Guid SessionId, Guid SpaceId, long FocusRevision, string CheckpointHash, long InputSequence, string TaskJson);
public sealed record GoalActivity(Guid ActivityId, Guid TenantId, Guid SessionId, Guid GoalId, Guid ActorId,
    string RequestKey, string Fingerprint, string Action, long Revision, long PlanRevision, DateTimeOffset At,
    Guid? CheckpointId = null, Guid? RunId = null, string? Note = null, GoalPlan? PriorPlan = null, GoalPlan? NewPlan = null,
    GoalEvidence? Evidence = null, IReadOnlyDictionary<string, string>? LegacyMetadata = null);

public static class GoalRules
{
    public const int MaxGoals = 20, MaxCheckpoints = 40, MaxDependencies = 8, MaxEvidence = 16, MaxAutoRuns = 5;
    public static string Task(SessionGoal goal, GoalCheckpointPlan checkpoint) => JsonSerializer.Serialize(new {
        goal.GoalId, goal.PlanRevision, goal.Plan.Objective, goal.Plan.SuccessCriteria, Checkpoint = checkpoint,
        Verified = goal.Checkpoints.Where(c => c.Status == GoalCheckpointStatus.Verified).Select(c => c.CheckpointId),
        RemainingAutomaticRuns = Math.Max(0, (goal.Plan.AutoContinuation?.MaxRuns ?? 0) - goal.AutoRunsUsed), goal.Plan.DeadlineAt });
    public static string Hash<T>(T value) => EvaluationRules.Hash(value);
    public static T Freeze<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static GoalPlan Validate(GoalPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Text(plan.Title, 200); Text(plan.Objective, 4000); Text(plan.SuccessCriteria, 4000);
        if (plan.Checkpoints is null || plan.Checkpoints.Count is < 1 or > MaxCheckpoints
            || plan.Checkpoints.Any(c => c is null) || !plan.Checkpoints.Any(c => c.Required)) throw new ArgumentException("Use 1–40 checkpoints with at least one required outcome.");
        var earlier = new HashSet<Guid>();
        foreach (var checkpoint in plan.Checkpoints)
        {
            if (checkpoint is null || checkpoint.CheckpointId == Guid.Empty || earlier.Contains(checkpoint.CheckpointId)) throw new ArgumentException("Checkpoint identities must be unique.");
            Text(checkpoint.Title, 200); Text(checkpoint.ExpectedOutcome, 4000);
            if (checkpoint.Dependencies is null || checkpoint.Dependencies.Count > MaxDependencies || checkpoint.Dependencies.Distinct().Count() != checkpoint.Dependencies.Count
                || checkpoint.Dependencies.Any(id => !earlier.Contains(id))) throw new ArgumentException("Dependencies must name at most eight distinct earlier checkpoints.");
            if (checkpoint.Verification is null || checkpoint.Verification.Count is < 1 or > 3 || checkpoint.Verification.Any(v => v is null) || checkpoint.Verification.Select(v => v.Kind).Distinct().Count() != checkpoint.Verification.Count)
                throw new ArgumentException("Choose 1–3 distinct verification methods (all must pass).");
            foreach (var rule in checkpoint.Verification)
                if (!Enum.IsDefined(rule.Kind) || rule.Kind == GoalVerificationKind.EvaluationPass
                    && (rule.SuiteId is null || rule.SuiteId == Guid.Empty || rule.SuiteVersion is null or < 1 || rule.SuiteHash is not { Length: 64 } || rule.SuiteHash.Any(c => !char.IsAsciiHexDigit(c)))
                    || rule.Kind != GoalVerificationKind.EvaluationPass && (rule.SuiteId is not null || rule.SuiteVersion is not null || rule.SuiteHash is not null))
                    throw new ArgumentException("Verification requires an exact Suite version/hash or explicit human/Artifact publication proof.");
            earlier.Add(checkpoint.CheckpointId);
        }
        var auto = plan.AutoContinuation ?? new();
        if (auto.Enabled && (auto.MaxRuns is < 1 or > MaxAutoRuns || auto.Until is null || auto.Until <= now || auto.Until > now.AddHours(24))
            || !auto.Enabled && (auto.MaxRuns != 0 || auto.Until is not null)) throw new ArgumentException("Automatic continuation requires 1–5 total successors and a deadline within 24 hours.");
        if (plan.DeadlineAt is { Offset: var offset } && offset != TimeSpan.Zero || auto.Until is { Offset: var autoOffset } && autoOffset != TimeSpan.Zero)
            throw new ArgumentException("Use UTC deadlines.");
        return Freeze(plan with { AutoContinuation = auto });
    }
    public static void Text(string? value, int max)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new ArgumentException($"Text must contain 1–{max} safe characters."); }
    public static bool DependenciesVerified(SessionGoal goal, Guid checkpoint) => goal.Plan.Checkpoints.Single(c => c.CheckpointId == checkpoint).Dependencies
        .All(id => goal.Checkpoints.Single(c => c.CheckpointId == id).Status == GoalCheckpointStatus.Verified);
    public static GoalCheckpointPlan? Next(SessionGoal goal) => goal.Plan.Checkpoints.FirstOrDefault(c => goal.Checkpoints.Single(s => s.CheckpointId == c.CheckpointId).Status
        is not (GoalCheckpointStatus.Verified or GoalCheckpointStatus.Skipped) && DependenciesVerified(goal, c.CheckpointId));
    public static bool Verified(SessionGoal goal) => goal.Plan.Checkpoints.Where(c => c.Required).All(c => goal.Checkpoints.Single(s => s.CheckpointId == c.CheckpointId).Status == GoalCheckpointStatus.Verified);
    public static void PreserveVerified(SessionGoal before, GoalPlan next)
    {
        foreach (var checkpoint in before.Checkpoints.Where(c => c.Status == GoalCheckpointStatus.Verified))
        {
            var old = before.Plan.Checkpoints.Single(c => c.CheckpointId == checkpoint.CheckpointId);
            var replacement = next.Checkpoints.SingleOrDefault(c => c.CheckpointId == checkpoint.CheckpointId);
            if (replacement is null || Hash(old) != Hash(replacement)) throw new ArgumentException("Verified checkpoints and their criteria are immutable; create a new Goal for different outcomes.");
        }
    }
}
