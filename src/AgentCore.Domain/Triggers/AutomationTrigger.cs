namespace AgentCore.Domain.Triggers;

public enum AutomationTriggerKind { Schedule, Event, CoreEvent }

public abstract record AutomationTrigger
{
    public abstract AutomationTriggerKind Kind { get; }
    public abstract bool SemanticEquals(AutomationTrigger other);
    public static implicit operator AutomationTrigger(TriggerSchedule schedule) => new ScheduleTrigger(schedule);
}

public sealed record ScheduleTrigger : AutomationTrigger
{
    public ScheduleTrigger(TriggerSchedule schedule) => Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    public TriggerSchedule Schedule { get; }
    public override AutomationTriggerKind Kind => AutomationTriggerKind.Schedule;
    public override bool SemanticEquals(AutomationTrigger other) => other is ScheduleTrigger s && Schedule.SemanticEquals(s.Schedule);
}

public abstract record FilteredEventTrigger : AutomationTrigger
{
    protected FilteredEventTrigger(string? filterExpression, EventDispatch? dispatch)
    {
        FilterExpression = string.IsNullOrWhiteSpace(filterExpression) ? null : filterExpression.Trim();
        if (FilterExpression is not null && (System.Text.Encoding.UTF8.GetByteCount(FilterExpression) > 1024 || FilterExpression.Contains('\0')))
            throw new ArgumentException("Filter must be at most 1024 UTF-8 bytes.");
        Dispatch = dispatch ?? new EventDispatch();
    }
    public string? FilterExpression { get; }
    public EventDispatch Dispatch { get; }
}

public enum EventDispatchMode { EveryMatch, CoalesceLatest }
public sealed record EventDispatch
{
    public EventDispatch(EventDispatchMode mode = EventDispatchMode.EveryMatch, int? windowSeconds = null)
    {
        if (!Enum.IsDefined(mode) || (mode == EventDispatchMode.EveryMatch && windowSeconds is not null)
            || (mode == EventDispatchMode.CoalesceLatest && windowSeconds is not (>= 60 and <= 3600)))
            throw new ArgumentException("Dispatch requires everyMatch or coalesceLatest with a 60–3600 second window.");
        Mode = mode;
        WindowSeconds = windowSeconds;
    }
    public EventDispatchMode Mode { get; }
    public int? WindowSeconds { get; }
}

public sealed record EventTrigger : FilteredEventTrigger
{
    public EventTrigger(Guid eventId, string? filterExpression = null, EventDispatch? dispatch = null) : base(filterExpression, dispatch)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("Event is required.");
        EventId = eventId;
    }
    public Guid EventId { get; }
    public override AutomationTriggerKind Kind => AutomationTriggerKind.Event;
    public override bool SemanticEquals(AutomationTrigger other) => other is EventTrigger e && e.EventId == EventId
        && e.FilterExpression == FilterExpression && e.Dispatch == Dispatch;
}

public sealed record CoreEventTrigger : FilteredEventTrigger
{
    public CoreEventTrigger(string coreEventKey, string? filterExpression = null, EventDispatch? dispatch = null) : base(filterExpression, dispatch)
    {
        if (!Events.CoreEventCatalog.Keys.Contains(coreEventKey, StringComparer.Ordinal))
            throw new ArgumentException("Core event type is unavailable.");
        CoreEventKey = coreEventKey;
    }
    public string CoreEventKey { get; }
    public override AutomationTriggerKind Kind => AutomationTriggerKind.CoreEvent;
    public override bool SemanticEquals(AutomationTrigger other) => other is CoreEventTrigger e && e.CoreEventKey == CoreEventKey
        && e.FilterExpression == FilterExpression && e.Dispatch == Dispatch;
}

public static class AutomationText
{
    public const int MaxNameCharacters = 120;
    public static string RequireInstructions(string value) => Require(value, TriggerLimits.MaxInstructionsCharacters, "Instructions");
    public static string RequireName(string value) => Require(value, MaxNameCharacters, "Name");
    private static string Require(string value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max || value.Contains('\0'))
            throw new ArgumentException($"{field} must contain 1–{max} characters.");
        return value.Trim();
    }
}

/// <summary>A stable child subscription. Configuration never grants execution authority.</summary>
public sealed record AutomationTriggerRecord
{
    public AutomationTriggerRecord(Guid triggerId, AutomationTrigger configuration, bool enabled = true, long revision = 1)
    {
        if (triggerId == Guid.Empty || revision < 1) throw new ArgumentException("Trigger identity and positive revision are required.");
        TriggerId = triggerId; Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Enabled = enabled; Revision = revision;
    }
    public Guid TriggerId { get; }
    public AutomationTrigger Configuration { get; }
    public bool Enabled { get; }
    public long Revision { get; }
    public Events.EventSourceReference? Source => Configuration switch
    {
        CoreEventTrigger e => Events.EventSourceReference.Builtin(e.CoreEventKey),
        EventTrigger e => Events.EventSourceReference.Webhook(e.EventId),
        _ => null
    };
}
