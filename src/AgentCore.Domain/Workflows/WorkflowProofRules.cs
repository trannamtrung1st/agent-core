namespace AgentCore.Domain.Workflows;

public sealed record WorkflowProof(bool CanSucceed, string? ReasonCode, IReadOnlyList<string> RouteNodes,
    IReadOnlyList<string> RouteEdges, IReadOnlyList<string> ApplicableRequired, IReadOnlyList<string> ProvenRequired);

public static class WorkflowProofRules
{
    public static string BranchOutcome(WorkflowNode branch, IReadOnlyList<WorkflowStepOperation> operations)
    {
        var binding = branch.Branch ?? throw new ArgumentException("A closed branch binding is required.");
        var source = operations.SingleOrDefault(s => s.NodeId == binding.SourceNodeId);
        if (source is null || source.Status < WorkflowOperationStatus.Settled) throw new ArgumentException("A branch needs a saved prior result.");
        return binding.Fact switch {
            WorkflowFactKind.VerificationPassed when source.Kind == WorkflowNodeKind.Verify && source.Outcome is "passed" or "failed" => source.ProofAccepted ? "true" : "false",
            WorkflowFactKind.DecisionApproved when source.Kind == WorkflowNodeKind.HumanDecision && source.Outcome is "approve" or "reject" => source.ProofAccepted ? "true" : "false",
            WorkflowFactKind.TaskOutcome when source.Kind == WorkflowNodeKind.AgentTask && source.Outcome is "succeeded" or "failed" => source.Outcome,
            _ => throw new ArgumentException("The recorded fact does not match the closed branch type.")
        };
    }
    public static WorkflowProof Evaluate(WorkflowVersion version, IReadOnlyList<WorkflowStepOperation> operations, bool boundGoalSatisfied = false)
    {
        var graph = version.Graph.Read(); var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var steps = operations.ToDictionary(s => s.NodeId, StringComparer.Ordinal);
        var route = new List<string>(); var edges = new List<string>(); var current = graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start);
        bool accepted(WorkflowNode node)
        {
            if (!steps.TryGetValue(node.Id, out var step) || step.GraphHash != version.Graph.Hash || step.Status < WorkflowOperationStatus.Settled) return false;
            if (node.Kind == WorkflowNodeKind.AgentTask)
                return step.Outcome == "succeeded" && graph.Nodes.Any(n => n.Verification?.TaskNodeId == node.Id
                    && route.Contains(n.Id) && steps.TryGetValue(n.Id, out var verification) && verification.ProofAccepted && verification.Outcome == "passed"
                    && verification.Status >= WorkflowOperationStatus.Settled);
            return node.Kind switch {
                WorkflowNodeKind.Verify => step.ProofAccepted && step.Outcome == "passed",
                WorkflowNodeKind.HumanDecision => step.ProofAccepted && step.DecisionActorId is not null && step.Outcome == "approve",
                _ => false
            };
        }
        string? reason = null; var endedSuccess = false;
        for (var count = 0; count < WorkflowGraphRules.MaxNodes; count++)
        {
            if (route.Contains(current.Id)) { reason = "workflow_route_invalid"; break; }
            route.Add(current.Id);
            if (current.Kind == WorkflowNodeKind.EndFailure) { reason = "workflow_route_failed"; break; }
            if (current.Kind == WorkflowNodeKind.EndSuccess) { endedSuccess = true; break; }
            string outcome;
            if (current.Kind == WorkflowNodeKind.Start) outcome = "next";
            else if (!steps.TryGetValue(current.Id, out var step) || step.GraphHash != version.Graph.Hash || step.Status < WorkflowOperationStatus.Settled || step.Outcome is null)
            { reason = "workflow_evidence_pending"; break; }
            else
            {
                outcome = step.Outcome;
                if (current.Kind == WorkflowNodeKind.Branch && outcome != BranchOutcome(current, operations)) { reason = "workflow_branch_proof_mismatch"; break; }
            }
            var edge = graph.Edges.SingleOrDefault(e => e.From == current.Id && e.Outcome == outcome);
            if (edge is null) { reason = "workflow_route_invalid"; break; }
            if (current.Kind == WorkflowNodeKind.Branch && steps[current.Id].SelectedEdgeId != edge.Id) { reason = "workflow_branch_proof_mismatch"; break; }
            edges.Add(edge.Id); current = byId[edge.To];
        }
        var applicable = graph.Nodes.Where(n => n.Requirement == WorkflowRequirement.Global
            || n.Requirement == WorkflowRequirement.Branch && edges.Contains(n.RequiredOnEdgeId!)).ToArray();
        var proven = applicable.Where(n => route.Contains(n.Id) && accepted(n)).Select(n => n.Id).ToArray();
        if (endedSuccess && (proven.Length != applicable.Length || route.Any(id => byId[id].Kind is WorkflowNodeKind.AgentTask or WorkflowNodeKind.Verify or WorkflowNodeKind.HumanDecision && !accepted(byId[id]))))
            reason = "workflow_required_evidence_missing";
        if (endedSuccess && graph.SuccessGoal is not null && !boundGoalSatisfied) reason = "workflow_goal_verification_pending";
        return new(endedSuccess && reason is null, reason, route.AsReadOnly(), edges.AsReadOnly(), applicable.Select(n => n.Id).ToArray(), proven);
    }
}
