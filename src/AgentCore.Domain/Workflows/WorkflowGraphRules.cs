namespace AgentCore.Domain.Workflows;

public static partial class WorkflowGraphRules
{
    public static IReadOnlyList<WorkflowDiagnostic> Validate(WorkflowGraph graph)
    {
        var errors = new List<WorkflowDiagnostic>();
        void Error(string code, string message, string? node = null, string? edge = null) => errors.Add(new(code, message, node, edge));
        if (graph.SchemaVersion != 1 || graph.Nodes is null || graph.Edges is null || graph.Inputs is null
            || graph.AllowedOrigins is null || graph.CoordinatorInstanceIds is null
            || graph.Nodes.Count is < 2 or > MaxNodes || graph.Edges.Count is < 1 or > MaxEdges || graph.Inputs.Count > MaxInputs
            || graph.MaxTotalTaskSteps is < 8 or > 144 || graph.MaxTotalTaskSeconds is < 60 or > 900)
        { Error("graph_bounds", "Use schema 1 and bounded graph, inputs and task budget."); return errors; }
        if (graph.Nodes.Any(n => n is null || !Key(n.Id) || !Text(n.Name, 80) || !Enum.IsDefined(n.Kind) || !Enum.IsDefined(n.Requirement))
            || graph.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != graph.Nodes.Count
            || graph.Edges.Any(e => e is null || !Key(e.Id) || !Key(e.From) || !Key(e.To) || !Text(e.Outcome, 40))
            || graph.Edges.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != graph.Edges.Count)
        { Error("graph_identity", "Use unique stable node/edge IDs, bounded names and known node kinds."); return errors; }
        if (graph.AllowedOrigins.Count is < 1 or > 3 || graph.AllowedOrigins.Any(o => !Enum.IsDefined(o))
            || graph.AllowedOrigins.Distinct().Count() != graph.AllowedOrigins.Count
            || graph.CoordinatorInstanceIds.Count > 16 || graph.CoordinatorInstanceIds.Any(i => !Id(i))
            || graph.CoordinatorInstanceIds.Distinct().Count() != graph.CoordinatorInstanceIds.Count
            || graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask) && graph.CoordinatorInstanceIds.Count == 0)
            Error("participants", "Declare bounded invocation origins and actual eligible coordinator identities.");
        foreach (var input in graph.Inputs)
            if (input is null || !Key(input.Key) || !Text(input.Label, 80) || !Enum.IsDefined(input.Kind)
                || input.MaxLength is < 1 or > 4000 || input.Kind == WorkflowInputKind.Choice && (input.Choices is null || input.Choices.Count is < 1 or > 16
                    || input.Choices.Any(v => !Text(v, 80)) || input.Choices.Distinct(StringComparer.Ordinal).Count() != input.Choices.Count)
                || input.Kind != WorkflowInputKind.Choice && input.Choices is not null)
                Error("input_schema", "Use a bounded closed input declaration.");
        if (graph.Inputs.Where(i => i is not null).Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != graph.Inputs.Count)
            Error("input_identity", "Input keys must be unique.");

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (graph.SuccessGoal is { } successGoal && (!Id(successGoal.SessionId) || !Id(successGoal.GoalId) || successGoal.PlanRevision < 1))
            Error("success_goal_binding", "Bind an exact Session Goal plan for final success.");
        foreach (var edge in graph.Edges)
            if (!nodes.ContainsKey(edge.From) || !nodes.ContainsKey(edge.To) || edge.From == edge.To)
                Error("edge_endpoint", "Edges require distinct existing endpoints.", edge: edge.Id);
        var starts = graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.Start).ToArray();
        if (starts.Length != 1) Error("start_count", "Exactly one Start is required.");
        if (!graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.EndSuccess)) Error("success_missing", "A reachable legitimate success exit is required.");
        if (errors.Any(e => e.Code is "edge_endpoint" or "start_count" or "success_missing")) return errors;
        var outgoing = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Where(e => e.From == n.Id).ToArray());
        var incoming = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Where(e => e.To == n.Id).ToArray());
        var ordered = Topological(graph, outgoing, incoming);
        if (ordered.Count != nodes.Count) { Error("cycle", "Workflow must be a DAG; loops are not supported."); return errors; }
        var start = starts[0].Id;
        var taskPath = RemainingTaskCount(graph, start);
        if (taskPath * 8 > graph.MaxTotalTaskSteps || taskPath * 60 > graph.MaxTotalTaskSeconds)
            Error("task_budget", "The process budget must reserve at least eight steps and sixty seconds per Task on its longest route.");
        var reachable = Reach(start, outgoing);
        foreach (var node in graph.Nodes)
        {
            if (!reachable.Contains(node.Id)) Error("unreachable", "Node is unreachable from Start.", node.Id);
            var edges = outgoing[node.Id];
            string[] outcomes = node.Kind switch {
                WorkflowNodeKind.EndSuccess or WorkflowNodeKind.EndFailure => [],
                WorkflowNodeKind.HumanDecision => ["approve", "reject"],
                WorkflowNodeKind.Verify => ["passed", "failed"],
                WorkflowNodeKind.AgentTask => ["succeeded", "failed"],
                WorkflowNodeKind.Branch when node.Branch?.Fact == WorkflowFactKind.TaskOutcome => ["succeeded", "failed"],
                WorkflowNodeKind.Branch => ["true", "false"], _ => ["next"] };
            if (!edges.Select(e => e.Outcome).Order(StringComparer.Ordinal).SequenceEqual(outcomes.Order(StringComparer.Ordinal)))
                Error("outcomes", "Provide exactly the labeled outcomes: " + string.Join(", ", outcomes), node.Id);
            if (node.Kind == WorkflowNodeKind.Start && incoming[node.Id].Length != 0) Error("start_incoming", "Start cannot have incoming edges.", node.Id);
            if ((node.Kind == WorkflowNodeKind.AgentTask) != (node.Task is not null)
                || (node.Kind == WorkflowNodeKind.Verify) != (node.Verification is not null)
                || (node.Kind == WorkflowNodeKind.HumanDecision) != (node.Decision is not null)
                || (node.Kind == WorkflowNodeKind.Branch) != (node.Branch is not null))
                Error("node_properties", "Only properties belonging to the node kind are allowed.", node.Id);
            if (node.Task is { } task && (!Id(task.TargetInstanceId) || !Id(task.SpaceId) || task.EnvironmentId == Guid.Empty || !Text(task.Objective, 4000)))
                Error("task_binding", "Bind an actual target, Space and bounded objective.", node.Id);
            if (node.Decision is { } decision && (!Id(decision.ReviewerPrincipalId) || !Text(decision.Prompt, 1000) || decision.TimeoutHours is < 1 or > 720))
                Error("reviewer_binding", "Bind an actual reviewer and bounded process question.", node.Id);
            if (node.Verification is { } proof)
            {
                var evaluation = proof.Kind == WorkflowVerificationKind.Evaluation;
                var goal = proof.Kind == WorkflowVerificationKind.GoalCheckpoint;
                if (!Enum.IsDefined(proof.Kind)
                    || evaluation && (!Key(proof.TaskNodeId) || !nodes.TryGetValue(proof.TaskNodeId!, out var taskNode) || taskNode.Kind != WorkflowNodeKind.AgentTask
                        || !Id(proof.SuiteId) || proof.SuiteVersion is null or < 1 || !Hash(proof.SuiteHash)
                        || proof.SessionId is not null || proof.GoalId is not null || proof.CheckpointId is not null || proof.PlanRevision is not null)
                    || goal && (!Id(proof.SessionId) || !Id(proof.GoalId) || !Id(proof.CheckpointId) || proof.PlanRevision is null or < 1
                        || proof.TaskNodeId is not null || proof.SuiteId is not null || proof.SuiteVersion is not null || proof.SuiteHash is not null))
                    Error("verification_binding", "Bind exact independent Evaluation or Goal checkpoint evidence.", node.Id);
                else if (evaluation && !Dominates(start, proof.TaskNodeId!, node.Id, outgoing))
                    Error("verification_order", "The source Task must precede verification on every route.", node.Id);
            }
            if (node.Branch is { } branch)
            {
                var expectedKind = branch.Fact switch { WorkflowFactKind.VerificationPassed => WorkflowNodeKind.Verify,
                    WorkflowFactKind.DecisionApproved => WorkflowNodeKind.HumanDecision, _ => WorkflowNodeKind.AgentTask };
                if (!Enum.IsDefined(branch.Fact) || !Key(branch.SourceNodeId) || !nodes.TryGetValue(branch.SourceNodeId, out var source)
                    || source.Kind != expectedKind || !Dominates(start, source.Id, node.Id, outgoing))
                    Error("branch_source", "Branch facts must come from a matching prior node on every route.", node.Id);
            }
            if (node.Kind is WorkflowNodeKind.Start or WorkflowNodeKind.Branch or WorkflowNodeKind.EndSuccess or WorkflowNodeKind.EndFailure
                && node.Requirement != WorkflowRequirement.None)
                Error("required_kind", "Only evidence-bearing steps can be required.", node.Id);
            if (node.Requirement == WorkflowRequirement.Branch)
            {
                var branchEdge = graph.Edges.SingleOrDefault(e => e.Id == node.RequiredOnEdgeId);
                if (branchEdge is null || nodes[branchEdge.From].Kind is not (WorkflowNodeKind.Branch or WorkflowNodeKind.HumanDecision or WorkflowNodeKind.Verify or WorkflowNodeKind.AgentTask)
                    || !Reach(branchEdge.To, outgoing).Contains(node.Id) || !Dominates(start, branchEdge.From, node.Id, outgoing)
                    || Reach(start, outgoing, excludedEdge: branchEdge.Id).Contains(node.Id))
                    Error("required_branch", "A branch-local required step must belong exclusively to its declared route.", node.Id);
                else foreach (var success in graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.EndSuccess && Reach(branchEdge.To, outgoing).Contains(n.Id)))
                    if (!Dominates(branchEdge.To, node.Id, success.Id, outgoing)) Error("required_bypass", "Selected branch can reach success without its required step.", node.Id, branchEdge.Id);
            }
            else if (node.RequiredOnEdgeId is not null) Error("required_scope", "Only branch-local requirements name an edge.", node.Id);
            if (node.Requirement == WorkflowRequirement.Global)
                foreach (var success in graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.EndSuccess))
                    if (!Dominates(start, node.Id, success.Id, outgoing)) Error("required_bypass", "A success route bypasses this global required step.", node.Id);
            if (node.Kind is WorkflowNodeKind.HumanDecision or WorkflowNodeKind.Verify or WorkflowNodeKind.AgentTask)
            {
                var refused = edges.FirstOrDefault(e => e.Outcome is "reject" or "failed");
                if (refused is not null && Reach(refused.To, outgoing).Any(id => nodes[id].Kind == WorkflowNodeKind.EndSuccess))
                    Error("negative_success", "Rejected or failed evidence cannot reach success.", node.Id, refused.Id);
            }
        }
        // Every Task needs an independent Verify on every successful suffix, not merely a terminal Task result.
        foreach (var task in graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.AgentTask))
        {
            var proofNodes = graph.Nodes.Where(n => n.Verification is { Kind: WorkflowVerificationKind.Evaluation } v && v.TaskNodeId == task.Id).Select(n => n.Id).ToHashSet();
            if (Reach(task.Id, outgoing, excluded: proofNodes).Any(id => nodes[id].Kind == WorkflowNodeKind.EndSuccess))
                Error("task_proof_bypass", "Every success route after a Task needs independent verification.", task.Id);
        }
        return errors.AsReadOnly();
    }

    public static int RemainingTaskCount(WorkflowGraph graph, string from)
    {
        var outgoing = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Where(e => e.From == n.Id).ToArray());
        var incoming = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Where(e => e.To == n.Id).ToArray());
        var order = Topological(graph, outgoing, incoming);
        if (order.Count != graph.Nodes.Count || !outgoing.ContainsKey(from)) throw new ArgumentException("A closed DAG and existing source node are required.");
        var counts = new Dictionary<string, int>(); var nodes = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var id in order.AsEnumerable().Reverse()) counts[id] = (nodes[id].Kind == WorkflowNodeKind.AgentTask ? 1 : 0)
            + outgoing[id].Select(e => counts[e.To]).DefaultIfEmpty(0).Max();
        return counts[from];
    }

    private static List<string> Topological(WorkflowGraph graph, Dictionary<string, WorkflowEdge[]> outgoing, Dictionary<string, WorkflowEdge[]> incoming)
    {
        var counts = incoming.ToDictionary(p => p.Key, p => p.Value.Length);
        var pending = new Queue<string>(graph.Nodes.Where(n => counts[n.Id] == 0).Select(n => n.Id)); var ordered = new List<string>();
        while (pending.TryDequeue(out var id)) { ordered.Add(id); foreach (var e in outgoing[id]) if (--counts[e.To] == 0) pending.Enqueue(e.To); }
        return ordered;
    }
    internal static HashSet<string> Reach(string from, Dictionary<string, WorkflowEdge[]> outgoing, string? excludedNode = null,
        string? excludedEdge = null, HashSet<string>? excluded = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(from);
        while (pending.TryPop(out var id)) {
            if (id == excludedNode || excluded?.Contains(id) == true || !found.Add(id)) continue;
            foreach (var edge in outgoing[id]) if (edge.Id != excludedEdge) pending.Push(edge.To);
        }
        return found;
    }
    private static bool Dominates(string from, string required, string to, Dictionary<string, WorkflowEdge[]> outgoing)
        => Reach(from, outgoing).Contains(to) && !Reach(from, outgoing, required).Contains(to);
}
