namespace AgentCore.Contracts.Http;

public sealed record AutomationTiming(string Kind, string TimeZone = "UTC", string? AtUtc = null,
    int Interval = 1, string? LocalTime = null, int[]? Weekdays = null, string? AnchorAtUtc = null,
    string? EndAtUtc = null, string? StartDate = null, string? EndDate = null, int? MaxOccurrences = null);
public sealed record AutomationTriggerDto(string Kind, AutomationTiming? Schedule = null, string? EventId = null);
public sealed record AutomationExecutionTargetDto(string Kind, string? SessionId = null);
public sealed record AutomationCompletionDeliveryDto(string Kind, string? SessionId = null);
public sealed record AutomationRequest(long ExpectedRevision, bool Enabled, string Name, string Instructions, AutomationTriggerDto Trigger,
    string? ModelKey = null, string? ReasoningEffort = null, AutomationExecutionTargetDto? ExecutionTarget = null, AutomationCompletionDeliveryDto? CompletionDelivery = null, bool RequiresTools = false, bool RequiresVision = false);
public sealed record AutomationResponse(string AutomationId, long Revision, string Name, string Instructions, bool Enabled, string Status,
    AutomationTriggerDto Trigger, string AuthorizationOrigin, string? SourceSessionId, string? SourceEventId,
    string CreatedAt, string? NextRunAt, string? ModelKey, string? ReasoningEffort, string? EffectiveModelKey,
    string? LastAgentRunId, string? ExecutionStatus, string? Outcome, AutomationExecutionTargetDto ExecutionTarget, AutomationCompletionDeliveryDto CompletionDelivery, string? SuspensionReason, bool RequiresTools, bool RequiresVision);
public sealed record AutomationPolicy(bool AllowOneShot, bool AllowDaily, bool AllowWeekly, bool AllowFixedInterval,
    bool AllowIndefiniteRecurrence, int OneShotHorizonDays, int MinRecurrenceDays, int MinFixedIntervalSeconds, int MaxActiveRegistrations, bool AllowEvents = false);
public sealed record AutomationReview(IReadOnlyList<AutomationResponse> Items, AutomationPolicy? Policy = null);
