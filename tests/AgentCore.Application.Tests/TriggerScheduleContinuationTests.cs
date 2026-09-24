using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

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
        TriggerRegistrationStatus.Active,
        new DateTimeOffset(2026, 9, 23, 1, 49, 0, TimeSpan.Zero));

    private static readonly ScheduleConversationContext HelloLatest = HelloVietnam with
    {
        RegistrationId = RegistrationB,
        Revision = 1,
        NextOccurrenceAtUtc = new DateTimeOffset(2026, 9, 23, 1, 52, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task Another_at_time_authorizes_only_with_trusted_referent()
    {
        var authorizer = new HeuristicTriggerCommandAuthorizer();
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await authorizer.AuthorizeCurrentTurnAsync(
                "another at 8:52",
                "en",
                TriggerCommandAction.Create,
                HelloVietnam));
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Deny,
            await authorizer.AuthorizeCurrentTurnAsync(
                "another at 8:52",
                "en",
                TriggerCommandAction.Create));
    }

    [Fact]
    public async Task Referent_update_and_cancel_authorize_with_context()
    {
        var authorizer = new HeuristicTriggerCommandAuthorizer();
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await authorizer.AuthorizeCurrentTurnAsync(
                "move that to 8:53",
                "en",
                TriggerCommandAction.Update,
                HelloLatest));
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await authorizer.AuthorizeCurrentTurnAsync(
                "cancel that",
                "en",
                TriggerCommandAction.Cancel,
                HelloLatest));
    }

    [Fact]
    public async Task Informational_future_does_not_authorize_create_even_with_referent()
    {
        var authorizer = new HeuristicTriggerCommandAuthorizer();
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Deny,
            await authorizer.AuthorizeCurrentTurnAsync(
                "I'll be back at 9",
                "en",
                TriggerCommandAction.Create,
                HelloVietnam));
    }

    [Fact]
    public void Schedule_hello_at_849_vietnam_is_schedule_related_turn()
    {
        Assert.True(TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(
            "schedule Hello at 8:49 Vietnam time",
            "en"));
    }

    [Fact]
    public async Task Multi_turn_create_another_move_cancel_targets_latest_registration()
    {
        var harness = await TriggerScheduleRuntimeTests.StartHarnessForContinuationAsync();
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);

        Assert.True(await runtime.SubmitUserTextAsync(
            "schedule Hello at 8:49 Vietnam time"));
        await runtime.WaitUntilIdleAsync();
        var first = Assert.Single(await harness.Store.ListAsync(owner, null));
        Assert.Equal("Hello", first.Intent);
        Assert.Equal(new TimeOnly(8, 49), Assert.IsType<OneShotSchedule>(first.Schedule).LocalTime);

        Assert.True(await runtime.SubmitUserTextAsync("another at 8:52"));
        await runtime.WaitUntilIdleAsync();
        var rows = await harness.Store.ListAsync(owner, null);
        Assert.Equal(2, rows.Count);
        var second = rows.MaxBy(row => row.NextOccurrenceAtUtc);
        Assert.Equal("Hello", second!.Intent);
        Assert.Equal("Asia/Ho_Chi_Minh", Assert.IsType<OneShotSchedule>(second.Schedule).TimeZoneId);
        Assert.Equal(new TimeOnly(8, 52), Assert.IsType<OneShotSchedule>(second.Schedule).LocalTime);

        Assert.True(await runtime.SubmitUserTextAsync("move that to 8:53"));
        await runtime.WaitUntilIdleAsync();
        var moved = (await harness.Store.GetAsync(owner, second.RegistrationId))!;
        Assert.Equal(new TimeOnly(8, 53), Assert.IsType<OneShotSchedule>(moved.Schedule).LocalTime);
        Assert.Equal(TriggerRegistrationStatus.Active, (await harness.Store.GetAsync(owner, first.RegistrationId))!.Status);

        Assert.True(await runtime.SubmitUserTextAsync("cancel that"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TriggerRegistrationStatus.Cancelled, (await harness.Store.GetAsync(owner, second.RegistrationId))!.Status);
        Assert.Equal(TriggerRegistrationStatus.Active, (await harness.Store.GetAsync(owner, first.RegistrationId))!.Status);
    }
}
