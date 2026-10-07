namespace AgentCore.Domain.Triggers;

public enum AutomationTriggerKind { Schedule, Event }

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

public sealed record EventTrigger : AutomationTrigger
{
    public EventTrigger(Guid eventSourceId, string eventType)
    {
        if (eventSourceId == Guid.Empty) throw new ArgumentException("Event source is required.");
        if (eventType != "order.placed") throw new ArgumentException("Event type is not supported.");
        EventSourceId = eventSourceId;
        EventType = eventType;
    }
    public Guid EventSourceId { get; }
    public string EventType { get; }
    public override AutomationTriggerKind Kind => AutomationTriggerKind.Event;
    public override bool SemanticEquals(AutomationTrigger other) => other is EventTrigger e && e.EventSourceId == EventSourceId && e.EventType == EventType;
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
