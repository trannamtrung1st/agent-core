namespace AgentCore.Contracts.Http;

public sealed record EventSourceReferenceDto(string Kind, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Key = null, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EventId = null);

public sealed record AutomationTiming(string Kind, string TimeZone = "UTC", string? AtUtc = null,
    int Interval = 1, string? LocalTime = null, int[]? Weekdays = null, string? AnchorAtUtc = null,
    string? EndAtUtc = null, string? StartDate = null, string? EndDate = null, int? MaxOccurrences = null);
public sealed record AutomationDispatchDto(string Mode = "everyMatch", int? WindowSeconds = null);
public sealed record AutomationTriggerDto(string Kind, AutomationTiming? Schedule = null, string? EventId = null, string? CoreEventKey = null, string? FilterExpression = null, AutomationDispatchDto? Dispatch = null, string? TriggerId = null, bool Enabled = true, long Revision = 1, EventSourceReferenceDto? Source = null, bool? Eligible = null, string? EligibilityReason = null);
public sealed record AutomationExecutionTargetDto(string Kind, string? SessionId = null);
public sealed record AutomationCompletionDeliveryDto(string Kind, string? SessionId = null);
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record AutomationRequest(long ExpectedRevision, bool Enabled, string Name, string Instructions, IReadOnlyList<AutomationTriggerDto> Triggers,
    string? ModelKey = null, string? ReasoningEffort = null, AutomationExecutionTargetDto? ExecutionTarget = null, AutomationCompletionDeliveryDto? CompletionDelivery = null, bool RequiresTools = false, bool RequiresVision = false, string? PresetId = null, int? PresetVersion = null)
{
    // Construction helper for a single schedule or source; the wire always contains canonical children.
    public AutomationRequest(long ExpectedRevision, bool Enabled, string Name, string Instructions, AutomationTriggerDto Trigger,
        string? ModelKey = null, string? ReasoningEffort = null, AutomationExecutionTargetDto? ExecutionTarget = null, AutomationCompletionDeliveryDto? CompletionDelivery = null,
        bool RequiresTools = false, bool RequiresVision = false, string? PresetId = null, int? PresetVersion = null)
        : this(ExpectedRevision, Enabled, Name, Instructions, new[] { Canonical(Trigger) }, ModelKey, ReasoningEffort, ExecutionTarget, CompletionDelivery, RequiresTools, RequiresVision, PresetId, PresetVersion) { }
    private static AutomationTriggerDto Canonical(AutomationTriggerDto t) => t with
    {
        TriggerId = t.TriggerId ?? Guid.NewGuid().ToString("D"),
        Kind = t.Kind == "coreEvent" ? "event" : t.Kind,
        Source = t.Kind == "coreEvent" ? new("builtin", t.CoreEventKey) : t.Kind == "event" && t.Source is null ? new("webhook", EventId: t.EventId) : t.Source,
        EventId = null, CoreEventKey = null
    };
}
public sealed record AutomationResponse(string AutomationId, long Revision, string Name, string Instructions, bool Enabled, string Status,
    string AuthorizationOrigin, string? SourceSessionId, string? SourceEventId,
    string CreatedAt, string? NextRunAt, string? ModelKey, string? ReasoningEffort, string? EffectiveModelKey,
    string? LastAgentRunId, string? ExecutionStatus, string? Outcome, AutomationExecutionTargetDto ExecutionTarget, AutomationCompletionDeliveryDto CompletionDelivery, string? SuspensionReason, bool RequiresTools, bool RequiresVision, string? PresetId = null, int? PresetVersion = null, IReadOnlyList<AutomationTriggerDto>? Triggers = null);
public sealed record AutomationPolicy(bool AllowOneShot, bool AllowDaily, bool AllowWeekly, bool AllowFixedInterval,
    bool AllowIndefiniteRecurrence, int OneShotHorizonDays, int MinRecurrenceDays, int MinFixedIntervalSeconds, int MaxActiveRegistrations, bool AllowEvents = false, bool AllowCoreEvents = false);
public sealed record AutomationReview(IReadOnlyList<AutomationResponse> Items, AutomationPolicy? Policy = null);

public sealed record AutomationFilterTestRequest(string? Expression, System.Text.Json.JsonElement Event);
public sealed record CoreEventTypeResponse(string Key, bool Eligible, string? Reason, System.Text.Json.JsonElement Example, System.Text.Json.JsonElement? FieldSchema = null);
public sealed record AutomationPresetResponse(string PresetId, int PresetVersion, string Name, string Description, string Instructions, AutomationTriggerDto Trigger, bool Eligible, IReadOnlyList<string> Prerequisites);
