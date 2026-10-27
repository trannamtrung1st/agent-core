using AgentCore.Domain.Goals;
namespace AgentCore.Domain.Tests;
public sealed class GoalRulesTests
{
    private static GoalCheckpointPlan Checkpoint(Guid id) => new(id, "Outcome", "A human inspects the required result", true, [], [new(GoalVerificationKind.HumanAcceptance)]);
    [Fact]
    public void Plans_bound_ordered_dependencies_required_criteria_and_opt_in_authority()
    {
        var now = DateTimeOffset.UtcNow; var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var plan = new GoalPlan("Goal", "Outcome", "Criterion", [Checkpoint(first), Checkpoint(second) with { Dependencies = [first] }]);
        Assert.False(GoalRules.Validate(plan, now).AutoContinuation!.Enabled);
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { Checkpoints = [Checkpoint(first) with { Dependencies = [second] }, Checkpoint(second)] }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { Checkpoints = [Checkpoint(first), Checkpoint(first)] }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { Checkpoints = [Checkpoint(first) with { Required = false }] }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { Checkpoints = Enumerable.Range(0, 41).Select(_ => Checkpoint(Guid.NewGuid())).ToArray() }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { AutoContinuation = new(true, 6, now.AddMinutes(10)) }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { AutoContinuation = new(true, 1, null) }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { AutoContinuation = new(true, 1, now.AddHours(25)) }, now));
        Assert.Throws<ArgumentException>(() => GoalRules.Validate(plan with { Checkpoints = [Checkpoint(first) with { Verification = [new(GoalVerificationKind.EvaluationPass)] }] }, now));
        Assert.True(GoalRules.Validate(plan with { AutoContinuation = new(true, 5, now.AddHours(1)) }, now).AutoContinuation!.Enabled);
    }
}
