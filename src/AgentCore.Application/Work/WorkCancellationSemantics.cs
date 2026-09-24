using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public static class WorkCancellationSemantics
{
    public const string UncertainExternalEffect =
        "External action outcome is unknown; it may already have completed.";

    public const string CompletedExternalEffect = WorkKnownEffects.ExternalActionCompletedBeforeCancellation;

    public static string? InferKnownEffectSummary(WorkItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (WorkKnownEffects.IsHistoricalCompletedEffect(item.KnownEffectSummary))
        {
            return item.KnownEffectSummary;
        }

        return item.SideEffect.Disposition switch
        {
            WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate => UncertainExternalEffect,
            WorkSideEffectDisposition.Succeeded => CompletedExternalEffect,
            _ => item.KnownEffectSummary
        };
    }

    public static string? MergeKnownEffectSummary(WorkItem item, string? requested)
    {
        if (requested is not null)
        {
            return requested;
        }

        if (WorkKnownEffects.IsHistoricalCompletedEffect(item.KnownEffectSummary))
        {
            return WorkKnownEffects.ExternalActionCompletedBeforeCancellation;
        }

        return InferKnownEffectSummary(item);
    }
}
