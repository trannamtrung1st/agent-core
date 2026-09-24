namespace AgentCore.Domain.Work;

public static class WorkKnownEffects
{
    public const string ExternalActionCompleted = "An external action completed.";

    public const string ExternalActionCompletedBeforeCancellation =
        "An external action completed before cancellation.";

    public static string? PreserveCompletedExternalEffect(string? current) =>
        current ?? ExternalActionCompleted;

    public static bool IsHistoricalCompletedEffect(string? summary) =>
        string.Equals(summary, ExternalActionCompleted, StringComparison.Ordinal)
        || string.Equals(summary, ExternalActionCompletedBeforeCancellation, StringComparison.Ordinal);
}
