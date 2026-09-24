namespace AgentCore.Domain.Work;

public static class WorkKnownEffects
{
    public const string ExternalActionCompletedBeforeCancellation =
        "An external action completed before cancellation.";

    public static string? PreserveCompletedExternalEffect(string? current) =>
        current ?? ExternalActionCompletedBeforeCancellation;

    public static bool IsHistoricalCompletedEffect(string? summary) =>
        string.Equals(summary, ExternalActionCompletedBeforeCancellation, StringComparison.Ordinal);
}
