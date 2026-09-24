using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public static class WorkCancellationSemantics
{
    public const string UncertainExternalEffect =
        "External action outcome is unknown; it may already have completed.";

    public static string? InferKnownEffectSummary(WorkItem? item)
    {
        if (item is null)
        {
            return null;
        }

        return item.SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate
            ? UncertainExternalEffect
            : item.KnownEffectSummary;
    }

    public static string? MergeKnownEffectSummary(WorkItem item, string? requested) =>
        requested ?? InferKnownEffectSummary(item);
}
