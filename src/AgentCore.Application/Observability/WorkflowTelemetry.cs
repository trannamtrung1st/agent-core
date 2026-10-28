using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Observability;

public enum WorkflowEvent { Accepted, Replayed, Projected, CancellationRequested, DecisionRecorded, TaskAdmitted, TaskReplayed }
public static class WorkflowTelemetry
{
    private static readonly Counter<long> Changes = RuntimeTelemetry.Meter.CreateCounter<long>("workflows.transitions");
    private static readonly Histogram<double> PendingAge = RuntimeTelemetry.Meter.CreateHistogram<double>("workflows.pending_age_seconds", "s");
    private static readonly Histogram<double> OperationAge = RuntimeTelemetry.Meter.CreateHistogram<double>("workflows.operation_age_seconds", "s");
    private static readonly Counter<long> Operations = RuntimeTelemetry.Meter.CreateCounter<long>("workflows.operations");
    private static readonly Counter<long> Conflicts = RuntimeTelemetry.Meter.CreateCounter<long>("workflows.bridge_conflicts");
    public static void Conflict() => Conflicts.Add(1);
    private static readonly Histogram<int> PageSize = RuntimeTelemetry.Meter.CreateHistogram<int>("workflows.recovery_page_size");
    private static readonly Histogram<double> PassDuration = RuntimeTelemetry.Meter.CreateHistogram<double>("workflows.recovery_duration_ms", "ms");
    public static void Record(WorkflowEvent action, WorkflowExecution execution)
    {
        Changes.Add(1, new TagList { { "event", action.ToString() }, { "status", execution.Status.ToString() }, { "origin", execution.Source.Kind.ToString() } });
        Activity.Current?.AddEvent(new("workflow.transition", tags: new ActivityTagsCollection {
            { "tenant.id", execution.TenantId }, { "workflow.id", execution.WorkflowId }, { "execution.id", execution.ExecutionId },
            { "version", execution.Version }, { "revision", execution.Revision }, { "event", action.ToString() }, { "status", execution.Status.ToString() }, { "reason", execution.ReasonCode }
        }));
    }
    public static void Pending(WorkflowExecution execution, DateTimeOffset now) => PendingAge.Record(Math.Max(0, (now - execution.CreatedAt).TotalSeconds),
        new TagList { { "status", execution.Status.ToString() }, { "origin", execution.Source.Kind.ToString() } });
    public static void Operation(WorkflowStepOperation operation, DateTimeOffset now)
    {
        var tags = new TagList { { "kind", operation.Kind.ToString() }, { "status", operation.Status.ToString() } };
        Operations.Add(1, tags); OperationAge.Record(Math.Max(0, (now - operation.CreatedAt).TotalSeconds), tags);
        Activity.Current?.AddEvent(new("workflow.operation", tags: new ActivityTagsCollection { { "execution.id", operation.ExecutionId },
            { "operation.id", operation.OperationId }, { "node.id", operation.NodeId }, { "attempt", operation.Attempt }, { "status", operation.Status.ToString() } }));
    }
    public static void Recovery(int size, double milliseconds) { PageSize.Record(Math.Clamp(size, 0, 16)); PassDuration.Record(Math.Max(0, milliseconds)); }
}
