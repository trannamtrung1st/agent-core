namespace AgentCore.Contracts.Http;

public sealed record AdminScheduleTiming(string Kind, string TimeZone = "UTC", string? AtUtc = null,
    int Interval = 1, string? LocalTime = null, int[]? Weekdays = null, string? AnchorAtUtc = null,
    string? EndAtUtc = null, string? StartDate = null, string? EndDate = null, int? MaxOccurrences = null);
public sealed record AdminScheduleRequest(long ExpectedRevision, bool Enabled, string Intent, AdminScheduleTiming Schedule,
    string? ModelKey = null, string? ReasoningEffort = null);
public sealed record AdminScheduleResponse(string RegistrationId, long Revision, string Intent, bool Enabled, string Status,
    AdminScheduleTiming Schedule, string AuthorizationOrigin, string? SourceSessionId, string? SourceEventId,
    string CreatedAt, string? NextRunAt, string? ModelKey, string? ReasoningEffort, string? EffectiveModelKey,
    string? LastWorkItemId, string? ExecutionStatus);
public sealed record AdminSchedulePolicy(bool AllowOneShot, bool AllowDaily, bool AllowWeekly, bool AllowFixedInterval,
    bool AllowIndefiniteRecurrence, int OneShotHorizonDays, int MinRecurrenceDays, int MinFixedIntervalSeconds, int MaxActiveRegistrations);
public sealed record AdminScheduleReview(IReadOnlyList<AdminScheduleResponse> Items, AdminSchedulePolicy? Policy = null);
