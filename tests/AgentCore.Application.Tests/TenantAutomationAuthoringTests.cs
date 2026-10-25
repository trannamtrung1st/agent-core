using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Application.Tests;

public sealed class TenantAutomationAuthoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentDefinition Definition = SampleDefinitions.Examiner with {
        TriggerPolicy = new(true, true, true, true, true, true, 32, 365, 1, ["schedule", "coreEvent", "applicationEvent"], true, 60)
    };
    private static readonly TenantAgent Agent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), Definition.Id, Definition.Version, Definition.Identity, AgentInstanceLifecycle.Active, Now, Now));
    private static TriggerSchedule Timing(string kind) => kind switch {
        "once" => new OneShotSchedule(Now.AddMinutes(30), "UTC"),
        "fixed" => new FixedIntervalSchedule(60, Now),
        "daily" => new DailySchedule(1, new TimeOnly(9, 30), "UTC"),
        "weekly" => new WeeklySchedule(1, [DayOfWeek.Thursday], new TimeOnly(9, 30), "UTC"),
        _ => throw new ArgumentException(kind)
    };
    private static Automation Build(Automation? current, TenantAutomationChange change, DateTimeOffset at, AgentDefinition? definition = null, TenantAgent? agent = null) =>
        TenantAutomationAuthoring.Build(agent ?? Agent, definition ?? Definition, current, change, TenantAutomationPresetRules.Provenance(current, change, at), at,
            AgentCore.Infrastructure.Providers.Synthetic.SyntheticModelCatalog.Create(), new SystemIdGenerator(TimeProvider.System), null);
    private static TenantAutomationChange Change(Automation value, bool? enabled = null) => new(enabled ?? value.Status == AutomationStatus.Active, value.Name,
        value.Instructions, value.IsSchedule ? value.Schedule : null, value.ModelOverrideOfferingId, value.ModelOverrideReasoningEffort, value.RequiresTools, value.RequiresVision,
        value.IsSchedule ? null : value.Triggers.Select(t => new TenantAutomationTriggerChange(t.TriggerId, t.Configuration, t.Enabled, t.Revision)).ToArray(),
        value.CompletionDelivery.SessionId, value.ExecutionTarget.SessionId);

    [Theory] [InlineData("once")] [InlineData("fixed")] [InlineData("daily")] [InlineData("weekly")]
    public void Ordinary_edits_preserve_due_work_and_trigger_identity_while_rechecking_current_policy(string kind)
    {
        var original = Build(null, new(true, "Original", "Original task", Timing(kind)), Now);
        var at = Now.AddHours(1);
        var edited = Build(original, Change(original) with { Name = "Renamed", Instructions = "Updated task", RequiresTools = false }, at);
        Assert.Equal(original.NextOccurrenceAtUtc, edited.NextOccurrenceAtUtc);
        Assert.True(edited.NextOccurrenceAtUtc < at);
        Assert.Equal(original.Triggers[0].TriggerId, edited.Triggers[0].TriggerId);
        Assert.Equal(original.TriggerRevision, edited.TriggerRevision);
        Assert.Equal(original.OccurrenceCount, edited.OccurrenceCount);
        Assert.Equal(original.Provenance.CreatedAt, edited.Provenance.CreatedAt);
        Assert.Equal(at, edited.Provenance.UpdatedAt);
        Assert.Equal(original.Revision + 1, edited.Revision);
        Assert.ThrowsAny<Exception>(() => Build(original, Change(original), at, Definition with { TriggerPolicy = Definition.TriggerPolicy! with { Enabled = false } }));
    }
    [Theory] [InlineData("fixed")] [InlineData("daily")] [InlineData("weekly")]
    public void Disabling_preserves_progress_and_reenabling_deliberately_selects_a_future_occurrence(string kind)
    {
        var original = Build(null, new(true, "Original", "Task", Timing(kind)), Now);
        var disabled = Build(original, Change(original, false), Now.AddHours(1));
        Assert.Equal(original.NextOccurrenceAtUtc, disabled.NextOccurrenceAtUtc);
        Assert.Equal(original.TriggerRevision, disabled.TriggerRevision);
        var enabled = Build(disabled, Change(disabled, true), Now.AddHours(2));
        Assert.True(enabled.NextOccurrenceAtUtc > Now.AddHours(2));
        Assert.Equal(original.TriggerRevision, enabled.TriggerRevision);
    }
    [Fact]
    public void An_elapsed_one_shot_cannot_be_created_retimed_or_reenabled_as_new_work()
    {
        var original = Build(null, new(true, "Original", "Task", Timing("once")), Now);
        var disabled = Build(original, Change(original, false), Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => Build(disabled, Change(disabled, true), Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Build(null, new(true, "New", "Task", Timing("once")), Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Build(original, Change(original) with { Schedule = new OneShotSchedule(Now.AddMinutes(29), "UTC") }, Now.AddHours(1)));
    }
    [Theory] [InlineData("once")] [InlineData("fixed")] [InlineData("daily")] [InlineData("weekly")]
    public void A_timing_change_retains_child_identity_advances_its_revision_and_selects_new_work(string kind)
    {
        var original = Build(null, new(true, "Original", "Task", Timing(kind)), Now);
        var schedule = Timing(kind) switch {
            OneShotSchedule t => (TriggerSchedule)new OneShotSchedule(t.AtUtc.AddHours(2), t.TimeZoneId),
            FixedIntervalSchedule t => new FixedIntervalSchedule(120, t.AnchorAtUtc),
            DailySchedule t => new DailySchedule(t.IntervalDays, t.LocalTime.AddHours(2), t.TimeZoneId),
            WeeklySchedule t => new WeeklySchedule(t.IntervalWeeks, t.Weekdays, t.LocalTime.AddHours(2), t.TimeZoneId),
            _ => throw new InvalidOperationException()
        };
        var changed = Build(original, Change(original) with { Schedule = schedule }, Now.AddHours(1));
        Assert.Equal(original.Triggers[0].TriggerId, changed.Triggers[0].TriggerId);
        Assert.Equal(original.TriggerRevision + 1, changed.TriggerRevision);
        Assert.True(changed.NextOccurrenceAtUtc > Now.AddHours(1));
        Assert.NotEqual(original.NextOccurrenceAtUtc, changed.NextOccurrenceAtUtc);
    }
    [Theory] [InlineData("past")] [InlineData("skip")] [InlineData("count")] [InlineData("suspended")] [InlineData("permission")]
    public void Durable_authoring_rejects_forged_occurrence_progress_or_an_untyped_transition(string mode)
    {
        var original = Build(null, new(true, "Original", "Task", Timing("fixed")), Now);
        var at = Now.AddMinutes(10);
        var proposed = new Automation(original.AutomationId, original.Owner, mode == "suspended" ? AutomationStatus.SuspendedPolicy : original.Status,
            original.Instructions, original.Triggers, mode == "past" ? Now.AddSeconds(1) : mode == "skip" ? at.AddHours(3) : original.NextOccurrenceAtUtc,
            original.ExpiresAtUtc, mode == "count" ? 1 : 0, original.Revision + 1, original.TriggerRevision, original.Provenance.WithUpdated(at), null);
        Assert.Throws<ArgumentException>(() => TenantAutomationAuthoring.ValidateTransition(original, proposed, original.Revision, mode == "permission" ? "automations.delete" : "automations.update", at));
    }
    [Fact]
    public void Event_edits_preserve_children_unless_the_exact_configuration_changes_and_reject_source_identity_reuse()
    {
        var source = Guid.NewGuid();
        var current = Build(null, new(false, "Events", "Task", null, Events: [new(null, new CoreEventTrigger("run.completed")), new(null, new EventTrigger(source))]), Now);
        var same = Build(current, Change(current) with { Name = "Renamed" }, Now.AddMinutes(1));
        Assert.Equal(current.Triggers.Select(t => (t.TriggerId, t.Revision)), same.Triggers.Select(t => (t.TriggerId, t.Revision)));
        var inputs = Change(same).Events!.ToArray(); inputs[0] = inputs[0] with { Enabled = false };
        var changed = Build(same, Change(same) with { Events = inputs }, Now.AddMinutes(2));
        Assert.Equal(2, changed.Triggers[0].Revision); Assert.Equal(1, changed.Triggers[1].Revision);
        var forged = Change(changed).Events!.ToArray(); forged[1] = forged[1] with { Configuration = new EventTrigger(Guid.NewGuid()) };
        Assert.Throws<ArgumentException>(() => Build(changed, Change(changed) with { Events = forged }, Now.AddMinutes(3)));
        var stale = Change(changed).Events!.ToArray(); stale[0] = stale[0] with { ExpectedRevision = 1 };
        Assert.Throws<SecurityConflictException>(() => Build(changed, Change(changed) with { Events = stale }, Now.AddMinutes(3)));
    }
    [Fact]
    public void Cross_owner_terminal_and_unknown_child_updates_never_construct_a_replacement()
    {
        var current = Build(null, new(false, "Events", "Task", null, Events: [new(null, new EventTrigger(Guid.NewGuid()))]), Now);
        Assert.Throws<AccessDeniedException>(() => Build(current, Change(current), Now.AddMinutes(1), agent: Agent with { TenantId = Guid.NewGuid() }));
        Assert.Throws<ArgumentException>(() => Build(current.WithCancellation(2, Now), Change(current), Now.AddMinutes(1)));
        Assert.Throws<SecurityConflictException>(() => Build(current, Change(current) with { Events = [new(Guid.NewGuid(), new EventTrigger(Guid.NewGuid()), ExpectedRevision: 1)] }, Now.AddMinutes(1)));
    }
}
