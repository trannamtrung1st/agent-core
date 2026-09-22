namespace AgentCore.Application.Agents;

/// <summary>
/// Central compaction limits. The trigger window already contains the retained raw tail.
/// The read adds one lookahead row and does not load the lifetime transcript.
/// </summary>
public static class CompactionPolicy
{
    public const int TriggerEligibleEntries = 40;
    public const int RetainedRawEntries = 20;
    public const int MaxSourceEntries = 20;
    public const int MaxSourceCharacters = 24_000;
    public const int MaxSummaryCharacters = PromptContextBuilder.MaxSummaryCharacters;
    public const int LookaheadEntries = 1;
    public const int ReadLimit = TriggerEligibleEntries + LookaheadEntries;
}

public static class CompactionRejection
{
    public const string NotReady = "not_ready";
    public const string OversizedSource = "oversized_source";
    public const string Empty = "empty";
    public const string Oversized = "oversized";
    public const string Unsafe = "unsafe";
    public const string Malformed = "malformed";
    public const string ProviderFailure = "provider_failure";
    public const string Cancelled = "cancelled";
    public const string Boundary = "boundary";
}
