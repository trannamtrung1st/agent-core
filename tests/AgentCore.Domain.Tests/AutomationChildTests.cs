using AgentCore.Domain.Triggers;
namespace AgentCore.Domain.Tests;

public sealed class AutomationChildTests
{
    private static Automation Parent(IReadOnlyList<AutomationTriggerRecord> children) => new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()),
        AutomationStatus.Disabled, "Review evidence.", children, null, null, 0, 1, 1,
        new(TriggerAuthorizationOrigin.AdminOwner, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), null);
    private static AutomationTriggerRecord Event(string? filter = null) => new(Guid.NewGuid(), new EventTrigger(Guid.NewGuid(), filter));

    [Fact]
    public void Independent_event_collection_copies_input_and_bounds_fanout()
    {
        var children = Enumerable.Range(0, 32).Select(_ => Event()).ToArray();
        var saved = Parent(children); var original = saved.Triggers[0]; children[0] = Event();
        Assert.Equal(original, saved.Triggers[0]); Assert.Equal(32, saved.Triggers.Count);
        Assert.Throws<ArgumentException>(() => Parent([.. children, Event()]));
    }
    [Fact]
    public void Mixed_modes_duplicate_sources_and_duplicate_child_ids_are_rejected()
    {
        var first = Event();
        Assert.Throws<ArgumentException>(() => Parent([first, new(Guid.NewGuid(), first.Configuration)]));
        Assert.Throws<ArgumentException>(() => Parent([first, new(first.TriggerId, new EventTrigger(Guid.NewGuid()))]));
        Assert.Throws<ArgumentException>(() => Parent([first, new(Guid.NewGuid(), new ScheduleTrigger(new OneShotSchedule(DateTimeOffset.UnixEpoch, "UTC")))]));
    }
    [Fact]
    public void Serialized_budget_bounds_unicode_configuration_below_the_child_count_limit()
    {
        var expression = "event.data.note === '" + new string('界', 320) + "'";
        Assert.Throws<ArgumentException>(() => Parent(Enumerable.Range(0, 32).Select(_ => Event(expression)).ToArray()));
        Assert.Equal(2, Parent([Event(expression), Event(expression)]).Triggers.Count);
    }
}
