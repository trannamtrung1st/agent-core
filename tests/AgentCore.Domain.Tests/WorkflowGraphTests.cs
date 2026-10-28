using System.Text.Json;
using AgentCore.Domain.Workflows;

namespace AgentCore.Domain.Tests;

public sealed class WorkflowGraphTests
{
    private static readonly Guid Target = Guid.Parse("05ca6f93-05dc-4697-a420-3c5d247f7070");
    private static readonly Guid Space = Guid.Parse("ed15b850-d295-429b-a9b1-f776af06e922");
    private static readonly Guid Reviewer = Guid.Parse("44fed2cc-3bb9-41cc-a530-31409f57d566");
    private static readonly Guid Suite = Guid.Parse("ace53b18-82d5-4e75-92e6-442e25a8c4d2");
    internal static WorkflowGraph Valid() => new(1, [
        new("start", "Start", WorkflowNodeKind.Start),
        new("prepare", "Gather material", WorkflowNodeKind.AgentTask, WorkflowRequirement.Global,
            Task: new(Target, "Gather the requested material", Space)),
        new("verify", "Verify completeness", WorkflowNodeKind.Verify, WorkflowRequirement.Global,
            Verification: new(WorkflowVerificationKind.Evaluation, "prepare", Suite, 1, new('a', 64))),
        new("sign", "Human sign-off", WorkflowNodeKind.HumanDecision, WorkflowRequirement.Global, Decision: new(Reviewer, "Accept the verified material?")),
        new("done", "Workflow completed", WorkflowNodeKind.EndSuccess),
        new("failed", "Process refused", WorkflowNodeKind.EndFailure)
    ], [new("a", "start", "prepare"), new("b", "prepare", "verify", "succeeded"), new("c", "prepare", "failed", "failed"),
        new("d", "verify", "sign", "passed"), new("e", "verify", "failed", "failed"),
        new("f", "sign", "done", "approve"), new("g", "sign", "failed", "reject")], [], [WorkflowInvocationKind.HumanManual], [Target]);

    [Fact]
    public void Verified_process_is_valid_and_canonical_order_does_not_change_hash()
    {
        var graph = Valid(); Assert.Empty(WorkflowGraphRules.Validate(graph));
        var first = WorkflowGraphRules.ValidateAndFreeze(graph);
        var second = WorkflowGraphRules.ValidateAndFreeze(graph with { Nodes = graph.Nodes.Reverse().ToArray(), Edges = graph.Edges.Reverse().ToArray() });
        Assert.Equal(first.Hash, second.Hash); Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first, new PublishedWorkflowGraph(first.CanonicalJson, first.Hash));
        Assert.Throws<ArgumentException>(() => new PublishedWorkflowGraph(first.CanonicalJson, new('0', 64)));
    }
    [Fact]
    public void Frozen_version_does_not_share_mutable_graph_collections()
    {
        var graph = Valid(); var nodes = graph.Nodes.ToArray(); var frozen = WorkflowGraphRules.ValidateAndFreeze(graph with { Nodes = nodes });
        nodes[1] = nodes[1] with { Name = "Changed draft" };
        Assert.DoesNotContain(frozen.Read().Nodes, n => n.Name == "Changed draft");
        var exposed = frozen.Read().Nodes; ((IList<WorkflowNode>)exposed)[0] = nodes[1];
        Assert.Equal(frozen.CanonicalJson, WorkflowGraphRules.ValidateAndFreeze(frozen.Read()).CanonicalJson);
    }
    [Fact]
    public void Alternate_success_edge_cannot_bypass_a_global_required_node()
    {
        var graph = Valid() with { Edges = Valid().Edges.Select(e => e.Id == "b" ? e with { To = "done" } : e).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(graph), e => e.Code == "required_bypass" && e.NodeId == "verify");
        Assert.Throws<ArgumentException>(() => WorkflowGraphRules.ValidateAndFreeze(graph));
    }
    [Fact]
    public void Process_budget_reserves_the_longest_Task_route_without_summing_exclusive_paths()
    {
        var graph = Valid();
        graph = graph with { Nodes = graph.Nodes.Concat([
            graph.Nodes.Single(n => n.Id == "prepare") with { Id = "prepare2" },
            graph.Nodes.Single(n => n.Id == "verify") with { Id = "verify2", Verification = new(WorkflowVerificationKind.Evaluation, "prepare2", Suite, 1, new('a', 64)) }]).ToArray(),
            Edges = graph.Edges.Select(e => e.Id == "d" ? e with { To = "prepare2" } : e).Concat([
                new WorkflowEdge("second", "prepare2", "verify2", "succeeded"), new WorkflowEdge("second-failure", "prepare2", "failed", "failed"),
                new WorkflowEdge("second-pass", "verify2", "sign", "passed"), new WorkflowEdge("second-reject", "verify2", "failed", "failed")]).ToArray() };
        Assert.Equal(2, WorkflowGraphRules.RemainingTaskCount(graph, "start"));
        Assert.Equal(1, WorkflowGraphRules.RemainingTaskCount(graph, "prepare2"));
        Assert.Empty(WorkflowGraphRules.Validate(graph with { MaxTotalTaskSteps = 16, MaxTotalTaskSeconds = 120 }));
        Assert.Contains(WorkflowGraphRules.Validate(graph with { MaxTotalTaskSteps = 8 }), d => d.Code == "task_budget");
        Assert.Contains(WorkflowGraphRules.Validate(graph with { MaxTotalTaskSeconds = 60 }), d => d.Code == "task_budget");
    }
    [Fact]
    public void Task_success_without_independent_verification_is_not_publishable()
    {
        var graph = Valid(); graph = graph with { Nodes = graph.Nodes.Where(n => n.Id != "verify").ToArray(),
            Edges = graph.Edges.Where(e => e.From != "verify").Select(e => e.To == "verify" ? e with { To = "sign" } : e).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(graph), e => e.Code == "task_proof_bypass" && e.NodeId == "prepare");
    }
    [Theory]
    [InlineData("g")]
    [InlineData("e")]
    [InlineData("c")]
    public void Rejection_and_failed_task_or_verification_never_reach_success(string edgeId)
    {
        var graph = Valid(); graph = graph with { Edges = graph.Edges.Select(e => e.Id == edgeId ? e with { To = "done" } : e).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(graph), e => e.Code == "negative_success" && e.EdgeId == edgeId);
    }
    [Fact]
    public void Cycle_is_reported_without_traversing_forever()
    {
        var graph = Valid(); graph = graph with { Edges = graph.Edges.Append(new WorkflowEdge("loop", "sign", "prepare", "repeat")).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(graph), e => e.Code == "cycle");
    }
    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void Unsupported_node_kind_is_refused(int kind)
    {
        var graph = Valid(); graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == "sign" ? n with { Kind = (WorkflowNodeKind)kind } : n).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(graph), e => e.Code == "graph_identity");
    }
    [Fact]
    public void Missing_endpoint_and_duplicate_ids_are_diagnostics()
    {
        var graph = Valid();
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Edges = graph.Edges.Append(new WorkflowEdge("bad", "start", "absent")).ToArray() }), e => e.Code == "edge_endpoint");
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Nodes = graph.Nodes.Append(graph.Nodes[0]).ToArray() }), e => e.Code == "graph_identity");
    }
    [Fact]
    public void Ambiguous_or_incomplete_outcomes_are_refused_with_diagnostics()
    {
        var graph = Valid();
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Edges = graph.Edges.Where(e => e.Id != "g").ToArray() }), e => e.Code == "outcomes" && e.NodeId == "sign");
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Edges = graph.Edges.Select(e => e.Id == "g" ? e with { Outcome = "approve" } : e).ToArray() }), e => e.Code == "outcomes");
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Edges = graph.Edges.Append(new WorkflowEdge("reject-again", "sign", "failed", "reject")).ToArray() }), e => e.Code == "outcomes");
    }
    [Fact]
    public void Branch_local_requirement_is_enforced_only_on_its_exclusive_route()
    {
        var graph = Valid();
        graph = graph with {
            Nodes = graph.Nodes.Select(n => n.Id == "sign" ? n with { Requirement = WorkflowRequirement.Branch, RequiredOnEdgeId = "yes" } : n).Concat([
                new WorkflowNode("branch", "Choose verified route", WorkflowNodeKind.Branch, Branch: new("verify", WorkflowFactKind.VerificationPassed)),
                new WorkflowNode("other", "Alternative accepted exit", WorkflowNodeKind.EndSuccess)]).ToArray(),
            Edges = graph.Edges.Select(e => e.Id == "d" ? e with { To = "branch" } : e).Concat([
                new WorkflowEdge("yes", "branch", "sign", "true"), new WorkflowEdge("no", "branch", "other", "false")]).ToArray()
        };
        Assert.Empty(WorkflowGraphRules.Validate(graph));
        var bypass = graph with { Edges = graph.Edges.Select(e => e.Id == "yes" ? e with { To = "done" } : e).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(bypass), e => e.Code == "required_branch" && e.NodeId == "sign");
    }
    [Fact]
    public void Future_fact_and_wrong_verification_source_are_refused()
    {
        var graph = Valid();
        var bad = graph with { Nodes = graph.Nodes.Select(n => n.Id == "verify" ? n with { Verification = n.Verification! with { TaskNodeId = "sign" } } : n).ToArray() };
        Assert.Contains(WorkflowGraphRules.Validate(bad), e => e.Code == "verification_binding");
    }
    [Fact]
    public void Draft_json_refuses_unknown_and_duplicate_properties_and_oversize()
    {
        var json = WorkflowGraphRules.ValidateAndFreeze(Valid()).CanonicalJson;
        Assert.Throws<ArgumentException>(() => WorkflowGraphRules.Parse(json.Insert(1, "\"script\":\"evil\",")));
        Assert.Throws<ArgumentException>(() => WorkflowGraphRules.Parse(json.Insert(1, "\"schemaVersion\":1,")));
        Assert.Throws<ArgumentException>(() => WorkflowGraphRules.Parse(new(' ', WorkflowGraphRules.MaxJsonBytes + 1)));
        var graph = Valid() with { Nodes = null! }; Assert.NotEmpty(WorkflowGraphRules.Validate(graph));
    }
    [Fact]
    public void Unicode_labels_are_preserved_but_invalid_utf16_is_refused()
    {
        var graph = Valid();
        Assert.Empty(WorkflowGraphRules.Validate(graph with { Nodes = graph.Nodes.Select(n => n.Id == "sign" ? n with { Name = "Chấp nhận ✅ 🎉" } : n).ToArray() }));
        Assert.Contains(WorkflowGraphRules.Validate(graph with { Nodes = graph.Nodes.Select(n => n.Id == "sign" ? n with { Name = "\ud800" } : n).ToArray() }), e => e.Code == "graph_identity");
    }
}
