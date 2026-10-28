using AgentCore.Domain.Workflows;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management;
using WorkflowDefinition = Elsa.Workflows.Management.Entities.WorkflowDefinition;
using Elsa.Workflows.Models;

namespace AgentCore.Infrastructure.Workflows;

internal sealed class CoreWorkflowMaterializer : IWorkflowMaterializer
{
    internal const string MaterializerName = "AgentCore.ClosedDag.v1";
    public string Name => MaterializerName;
    public ValueTask<Workflow> MaterializeAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (definition.MaterializerName != Name || definition.StringData is null || definition.MaterializerContext is null)
            throw new InvalidOperationException("Invalid closed workflow materialization.");
        var frozen = new PublishedWorkflowGraph(definition.StringData, definition.MaterializerContext);
        var graph = frozen.Read();
        var activities = graph.Nodes.ToDictionary(n => n.Id, n => new CoreWorkflowStep {
            Id = n.Id, Name = n.Name, CoreNodeId = n.Id, Kind = n.Kind,
            AllowedOutcomes = graph.Edges.Where(e => e.From == n.Id).Select(e => e.Outcome).ToArray()
        });
        var root = new Flowchart {
            Id = "core-flow", Start = activities[graph.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start).Id],
            Activities = activities.Values.Cast<IActivity>().ToList(),
            Connections = graph.Edges.Select(e => new Connection(new Endpoint(activities[e.From], e.Outcome), new Endpoint(activities[e.To]))).ToList()
        };
        var workflow = new Workflow(root) {
            Identity = new(definition.DefinitionId, definition.Version, definition.Id, definition.TenantId),
            Publication = WorkflowPublication.LatestAndPublished, IsReadonly = true
        };
        return ValueTask.FromResult(workflow);
    }
}

[FlowNode("next", "succeeded", "failed", "passed", "approve", "reject", "true", "false")]
internal sealed class CoreWorkflowStep : Activity
{
    public string CoreNodeId { get; set; } = "";
    public WorkflowNodeKind Kind { get; set; }
    public string[] AllowedOutcomes { get; set; } = [];
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        if (Kind == WorkflowNodeKind.Start) { await context.CompleteActivityWithOutcomesAsync("next"); return; }
        if (Kind == WorkflowNodeKind.EndFailure) { await Finish(context); return; }
        context.CreateBookmark(new CreateBookmarkArgs {
            BookmarkName = "AgentCore.WorkflowStep", Stimulus = CoreNodeId, Callback = ResumeAsync,
            Metadata = new Dictionary<string, string> { ["coreNodeId"] = CoreNodeId }
        });
    }
    private async ValueTask ResumeAsync(ActivityExecutionContext context)
    {
        var outcome = context.GetWorkflowInput<string>("coreOutcome");
        var node = context.GetWorkflowInput<string>("coreNodeId");
        if (node != CoreNodeId || (Kind == WorkflowNodeKind.EndSuccess ? outcome != "complete" : !AllowedOutcomes.Contains(outcome, StringComparer.Ordinal)))
            throw new InvalidOperationException("Closed workflow signal mismatch.");
        if (Kind == WorkflowNodeKind.EndSuccess) { await Finish(context); return; }
        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
    private async ValueTask Finish(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Properties["coreTerminalNodeId"] = CoreNodeId;
        await context.CompleteActivityAsync();
    }
}
