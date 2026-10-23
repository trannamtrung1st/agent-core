using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Application.Tests;

public sealed class TriggerScheduleContinuationTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-00b1-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00b1-7000-8000-0000000000b1");
    private static readonly Guid RegistrationA = Guid.Parse("019944af-00e1-7000-8000-0000000000c1");
    private static readonly Guid RegistrationB = Guid.Parse("019944af-00e1-7000-8000-0000000000c2");

    private static readonly ScheduleConversationContext HelloVietnam = new(
        RegistrationA,
        1,
        TriggerCommandAction.Create,
        "Hello",
        "Asia/Ho_Chi_Minh",
        TriggerScheduleKind.OneShot,
        AutomationStatus.Active,
        new DateTimeOffset(2026, 9, 23, 1, 49, 0, TimeSpan.Zero));

    private static readonly ScheduleConversationContext HelloLatest = HelloVietnam with
    {
        AutomationId = RegistrationB,
        Revision = 1,
        NextOccurrenceAtUtc = new DateTimeOffset(2026, 9, 23, 1, 52, 0, TimeSpan.Zero)
    };

    [Theory]
    [InlineData("null")]
    [InlineData("\"Event\"")]
    [InlineData("\"Unknown\"")]
    [InlineData("\"999\"")]
    [InlineData("42")]
    public void Event_or_invalid_tool_results_cannot_become_schedule_referents(string scheduleKind)
    {
        var json = $$"""{"automationId":"{{RegistrationA}}","instructions":"Review orders","triggerKind":"Event","scheduleKind":{{scheduleKind}},"timeZone":null,"status":"Active"}""";
        Assert.Null(ScheduleConversationContext.TryFromRegistrationJson(json, TriggerCommandAction.Create));
        Assert.Null(ScheduleConversationContext.TryFromRegistrationJson(
            $$"""{"automationId":"{{RegistrationA}}","instructions":"Review orders","triggerKind":"Event"}""",
            TriggerCommandAction.Update));
    }

    [Theory]
    [InlineData(TriggerScheduleKind.OneShot)]
    [InlineData(TriggerScheduleKind.Daily)]
    [InlineData(TriggerScheduleKind.Weekly)]
    [InlineData(TriggerScheduleKind.FixedInterval)]
    public void Schedule_tool_results_preserve_kind_and_cancellation_state(TriggerScheduleKind kind)
    {
        var json = $$"""{"automationId":"{{RegistrationA}}","revision":2,"instructions":"Hello","scheduleKind":"{{kind}}","timeZone":"UTC","status":"Cancelled"}""";
        var referent = ScheduleConversationContext.TryFromRegistrationJson(json, TriggerCommandAction.Cancel);
        Assert.NotNull(referent);
        Assert.Equal(kind, referent.ScheduleKind);
        Assert.Equal("UTC", referent.TimeZoneId);
        Assert.Equal(TriggerCommandAction.Cancel, referent.LastAction);
        Assert.False(referent.IsReferentAvailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Event_tool_result_invalidates_only_the_same_schedule_referent(bool sameAutomation)
    {
        var id = sameAutomation ? RegistrationA : RegistrationB;
        var json = $$"""{"automationId":"{{id}}","instructions":"Review orders","triggerKind":"Event","scheduleKind":null,"timeZone":null,"status":"Active"}""";
        var referent = ScheduleConversationContext.RefreshFromRegistrationJson(
            HelloVietnam, json, TriggerCommandAction.Update);
        if (sameAutomation) Assert.Null(referent);
        else Assert.Equal(HelloVietnam, referent);
    }

    [Fact]
    public void Fixed_interval_reconstruction_uses_the_same_utc_timezone_as_tool_results()
    {
        var now = DateTimeOffset.UtcNow;
        var registration = new Automation(RegistrationA, new TriggerOwner(InstanceId, ProfileId),
            AutomationStatus.Active, "Hello", new FixedIntervalSchedule(3600, now, null, null),
            now, null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, now, now), null);
        var referent = ScheduleConversationContext.FromRegistration(registration, TriggerCommandAction.Create);
        Assert.Equal("UTC", referent.TimeZoneId);
        Assert.Equal(TriggerScheduleKind.FixedInterval, referent.ScheduleKind);
    }
}
