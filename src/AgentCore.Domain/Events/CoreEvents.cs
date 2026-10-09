using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Events;

public static class CoreEventCatalog
{
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    { "run.completed", "run.failed", "session.completed", "session.ended", "instance.config_changed", "harness.definition_adopted" });
}

public sealed record CoreEventOccurrence(Guid EventId, string DedupeKey, TriggerOwner Owner, string Key,
    DateTimeOffset OccurredAtUtc, string DataJson, Guid? RootAgentRunId = null, int TriggerDepth = 0,
    IReadOnlyList<Guid>? VisitedAutomationIds = null);

public enum EventMatchStatus { Pending, Matched, Filtered, FilterError, PolicySkipped, LoopSkipped, Coalesced, BudgetSkipped, Admitted }
public sealed record EventSubscriptionSnapshot(Guid AutomationId, TriggerOwner Owner, long TriggerRevision,
    string? FilterExpression, EventDispatch Dispatch, TriggerSourceKind SourceKind = TriggerSourceKind.CoreEvent, Guid? ResourceId = null)
{
    public string ExpressionVersion { get; init; } = "js-expression-v1";
    public string FilterHash => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FilterExpression ?? ""))).ToLowerInvariant();
}

public sealed record EventBucketSource(Guid EventId, TriggerOwner Owner, string Key, DateTimeOffset ReceivedAtUtc, string DataJson,
    Guid? RootAgentRunId = null, int TriggerDepth = 0, IReadOnlyList<Guid>? VisitedAutomationIds = null);
public sealed record EventFilterResult(bool? Matched, string Status, string? Code = null,
    int SchemaVersion = 1, string ExpressionVersion = "js-expression-v1");
