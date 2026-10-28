using AgentCore.Domain.Workflows;

namespace AgentCore.Domain.Tests;

public sealed class WorkflowProofTests
{
    private static (WorkflowVersion Version, WorkflowStepOperation[] Steps) Saved()
    {
        var tenant = Guid.NewGuid(); var execution = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var version = new WorkflowVersion(tenant, id, 1, WorkflowGraphRules.ValidateAndFreeze(WorkflowGraphTests.Valid()), Guid.NewGuid(), now);
        WorkflowStepOperation Step(string node, string outcome, bool proof, Guid? human = null) => new(tenant, execution, node, 1, Guid.NewGuid(),
            node, version.Graph.Hash, version.Graph.Read().Nodes.Single(n => n.Id == node).Kind, 2, WorkflowOperationStatus.Settled,
            now, DecisionActorId: human, Outcome: outcome, ProofAccepted: proof, SettledAt: now);
        return (version, [Step("prepare", "succeeded", false), Step("verify", "passed", true), Step("sign", "approve", true, Guid.NewGuid())]);
    }
    [Fact]
    public void Successful_worker_alone_cannot_prove_completion()
    {
        var (version, steps) = Saved();
        Assert.False(WorkflowProofRules.Evaluate(version, steps.Take(1).ToArray()).CanSucceed);
        var proof = WorkflowProofRules.Evaluate(version, steps); Assert.True(proof.CanSucceed);
        Assert.Equal(3, proof.ProvenRequired.Count); Assert.Equal(3, proof.ApplicableRequired.Count);
    }
    [Theory] [InlineData("verify")] [InlineData("sign")]
    public void Selected_route_requires_positive_evidence_even_for_optional_nodes(string node)
    {
        var (version, steps) = Saved();
        var graph = version.Graph.Read();
        version = version with { Graph = WorkflowGraphRules.ValidateAndFreeze(graph with { Nodes = graph.Nodes.Select(n => n.Id == node ? n with { Requirement = WorkflowRequirement.None } : n).ToArray() }) };
        steps = steps.Select(s => s with { GraphHash = version.Graph.Hash, ProofAccepted = s.NodeId == node ? false : s.ProofAccepted }).ToArray();
        Assert.Equal("workflow_required_evidence_missing", WorkflowProofRules.Evaluate(version, steps).ReasonCode);
    }
    [Fact]
    public void Negative_outcome_stays_failed_and_cannot_reuse_prior_positive_proof()
    {
        var (version, steps) = Saved();
        steps[1] = steps[1] with { Outcome = "failed", ProofAccepted = false };
        var proof = WorkflowProofRules.Evaluate(version, steps);
        Assert.False(proof.CanSucceed); Assert.DoesNotContain("sign", proof.RouteNodes);
    }
    [Fact]
    public void Bound_goal_requires_its_own_authoritative_success_fact()
    {
        var (version, steps) = Saved(); var graph = version.Graph.Read();
        version = version with { Graph = WorkflowGraphRules.ValidateAndFreeze(graph with { SuccessGoal = new(Guid.NewGuid(), Guid.NewGuid(), 1) }) };
        steps = steps.Select(s => s with { GraphHash = version.Graph.Hash }).ToArray();
        Assert.Equal("workflow_goal_verification_pending", WorkflowProofRules.Evaluate(version, steps).ReasonCode);
        Assert.True(WorkflowProofRules.Evaluate(version, steps, true).CanSucceed);
    }
    [Fact]
    public void Missing_or_unsettled_fact_cannot_select_branch()
    {
        var (_, steps) = Saved();
        var branch = new WorkflowNode("branch", "Prior verification", WorkflowNodeKind.Branch, Branch: new("verify", WorkflowFactKind.VerificationPassed));
        Assert.Equal("true", WorkflowProofRules.BranchOutcome(branch, steps));
        Assert.Throws<ArgumentException>(() => WorkflowProofRules.BranchOutcome(branch, []));
        Assert.Throws<ArgumentException>(() => WorkflowProofRules.BranchOutcome(branch, steps.Select(s => s with { Status = WorkflowOperationStatus.Admitted }).ToArray()));
    }
}
