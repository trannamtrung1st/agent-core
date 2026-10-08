using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Tests;

public sealed class AutomationTests
{
    [Fact]
    public void EventTrigger_HasStableEventId_WithoutTiming()
    {
        var trigger = new EventTrigger(Guid.NewGuid());
        Assert.Equal(AutomationTriggerKind.Event, trigger.Kind);
        Assert.Throws<ArgumentException>(() => new EventTrigger(Guid.Empty));
        Assert.True(trigger.SemanticEquals(new EventTrigger(trigger.EventId)));
    }

    [Fact]
    public void ScheduleTrigger_PreservesValidatedTiming()
    {
        var timing = new FixedIntervalSchedule(3600, DateTimeOffset.UnixEpoch);
        var trigger = new ScheduleTrigger(timing);
        Assert.Equal(AutomationTriggerKind.Schedule, trigger.Kind);
        Assert.Same(timing, trigger.Schedule);
        Assert.Throws<ArgumentNullException>(() => new ScheduleTrigger(null!));
    }
}
