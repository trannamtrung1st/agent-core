namespace AgentCore.Contracts.Http;

public sealed record EventSourceReferenceDto(string Kind, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Key = null, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EventId = null);

public sealed record AutomationTiming(string Kind, string TimeZone = "UTC", string? AtUtc = null,
    int Interval = 1, string? LocalTime = null, int[]? Weekdays = null, string? AnchorAtUtc = null,
    string? EndAtUtc = null, string? StartDate = null, string? EndDate = null, int? MaxOccurrences = null);
public sealed record AutomationDispatchDto(string Mode = "everyMatch", int? WindowSeconds = null);
public sealed record AutomationTriggerDto(string Kind, AutomationTiming? Schedule = null, string? EventId = null, string? CoreEventKey = null, string? FilterExpression = null, AutomationDispatchDto? Dispatch = null, string? TriggerId = null, bool Enabled = true, long Revision = 1, EventSourceReferenceDto? Source = null, bool? Eligible = null, string? EligibilityReason = null);
public sealed record AutomationExecutionTargetDto(string Kind, string? SessionId = null);
public sealed record AutomationCompletionDeliveryDto(string Kind, string? SessionId = null);
public sealed record AutomationPolicy(bool AllowOneShot, bool AllowDaily, bool AllowWeekly, bool AllowFixedInterval,
    bool AllowIndefiniteRecurrence, int OneShotHorizonDays, int MinRecurrenceDays, int MinFixedIntervalSeconds, int MaxActiveRegistrations, bool AllowEvents = false, bool AllowCoreEvents = false);

public sealed record AutomationFilterTestRequest(string? Expression, System.Text.Json.JsonElement Event);
public sealed record CoreEventTypeResponse(string Key, bool Eligible, string? Reason, System.Text.Json.JsonElement Example, System.Text.Json.JsonElement? FieldSchema = null);
public sealed record AutomationPresetResponse(string PresetId, int PresetVersion, string Name, string Description, string Instructions, AutomationTriggerDto Trigger, bool Eligible, IReadOnlyList<string> Prerequisites);
