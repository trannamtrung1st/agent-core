namespace AgentCore.Contracts.Http;

public sealed record AutomationTiming(string Kind, string TimeZone = "UTC", string? AtUtc = null,
    int Interval = 1, string? LocalTime = null, int[]? Weekdays = null, string? AnchorAtUtc = null,
    string? EndAtUtc = null, string? StartDate = null, string? EndDate = null, int? MaxOccurrences = null);
public sealed record AutomationTriggerDto(string Kind, AutomationTiming? Schedule = null, string? EventSourceId = null, string? EventType = null);
public sealed record AutomationRequest(long ExpectedRevision, bool Enabled, string Name, string Instructions, AutomationTriggerDto Trigger,
    string? ModelKey = null, string? ReasoningEffort = null);
public sealed record AutomationResponse(string AutomationId, long Revision, string Name, string Instructions, bool Enabled, string Status,
    AutomationTriggerDto Trigger, string AuthorizationOrigin, string? SourceSessionId, string? SourceEventId,
    string CreatedAt, string? NextRunAt, string? ModelKey, string? ReasoningEffort, string? EffectiveModelKey,
    string? LastWorkItemId, string? ExecutionStatus, string? Outcome);
public sealed record AutomationPolicy(bool AllowOneShot, bool AllowDaily, bool AllowWeekly, bool AllowFixedInterval,
    bool AllowIndefiniteRecurrence, int OneShotHorizonDays, int MinRecurrenceDays, int MinFixedIntervalSeconds, int MaxActiveRegistrations);
public sealed record AutomationReview(IReadOnlyList<AutomationResponse> Items, AutomationPolicy? Policy = null);
