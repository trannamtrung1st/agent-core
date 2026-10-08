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
        Assert.Equal("Hello", first.Instructions);
        Assert.Equal(new TimeOnly(8, 49), Assert.IsType<OneShotSchedule>(first.Schedule).LocalTime);

        Assert.True(await runtime.SubmitUserTextAsync("another at 8:52"));
        await runtime.WaitUntilIdleAsync();
        var rows = await harness.Store.ListAsync(owner, null);
        Assert.Equal(2, rows.Count);
        var second = rows.MaxBy(row => row.NextOccurrenceAtUtc);
        Assert.Equal("Hello", second!.Instructions);
        Assert.Equal("Asia/Ho_Chi_Minh", Assert.IsType<OneShotSchedule>(second.Schedule).TimeZoneId);
        Assert.Equal(new TimeOnly(8, 52), Assert.IsType<OneShotSchedule>(second.Schedule).LocalTime);

        Assert.True(await runtime.SubmitUserTextAsync("move that to 8:53"));
        await runtime.WaitUntilIdleAsync();
        var moved = (await harness.Store.GetAsync(owner, second.AutomationId))!;
        Assert.Equal(new TimeOnly(8, 53), Assert.IsType<OneShotSchedule>(moved.Schedule).LocalTime);
        Assert.Equal(AutomationStatus.Active, (await harness.Store.GetAsync(owner, first.AutomationId))!.Status);

        Assert.True(await runtime.SubmitUserTextAsync("cancel that"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(AutomationStatus.Cancelled, (await harness.Store.GetAsync(owner, second.AutomationId))!.Status);
        Assert.Equal(AutomationStatus.Active, (await harness.Store.GetAsync(owner, first.AutomationId))!.Status);
    }

    [Fact]
    public async Task Reattached_runtime_reconstructs_schedule_referent_from_trigger_store()
    {
        var harness = await TriggerScheduleRuntimeTests.StartHarnessForContinuationAsync();
        await using (var runtime = harness.Runtime)
        {
            Assert.True(await runtime.SubmitUserTextAsync("schedule Hello at 8:49 Vietnam time"));
            await runtime.WaitUntilIdleAsync();
        }

        var reconstructed = await harness.Tools.TryReconstructScheduleConversationContextAsync(InstanceId, ProfileId);
        Assert.NotNull(reconstructed);
        var authorizer = new HeuristicTriggerCommandAuthorizer();
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await authorizer.AuthorizeCurrentTurnAsync(
                "another at 8:55",
                "en",
                TriggerCommandAction.Create,
                reconstructed));

        await using var reattached = await harness.ReattachRuntimeAsync();
        Assert.NotNull(await harness.Tools.TryReconstructScheduleConversationContextAsync(InstanceId, ProfileId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reattach_with_latest_event_automation_keeps_chat_usable_and_only_restores_schedule_referents(bool hasSchedule)
    {
        var harness = await TriggerScheduleRuntimeTests.StartHarnessForContinuationAsync();
        var owner = new TriggerOwner(InstanceId, ProfileId);
        Guid? scheduleId = null;
        await using (var runtime = harness.Runtime)
        {
            if (hasSchedule)
            {
                Assert.True(await runtime.SubmitUserTextAsync("schedule Hello at 8:49 Vietnam time"));
                await runtime.WaitUntilIdleAsync();
                scheduleId = Assert.Single(await harness.Store.ListAsync(owner, null)).AutomationId;
            }
        }

        var now = harness.Time.GetUtcNow().AddMinutes(1);
        var eventAutomation = new Automation(
            Guid.NewGuid(), owner, AutomationStatus.Active, "Review new orders",
            new EventTrigger(Guid.NewGuid()), null, null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, now, now), null);
        await harness.Store.CreateAsync(eventAutomation);
        Assert.Equal(eventAutomation.AutomationId, (await harness.Store.ListAsync(owner, null))[0].AutomationId);

        var referent = await harness.Tools.TryReconstructScheduleConversationContextAsync(InstanceId, ProfileId);
        Assert.Equal(scheduleId, referent?.AutomationId);

        await using var reattached = await harness.ReattachRuntimeAsync();
        Assert.Equal(SessionStatus.Attached, reattached.Snapshot.Status);
        Assert.True(await reattached.SubmitUserTextAsync("hi"));
        await reattached.WaitUntilIdleAsync();
        Assert.Contains(reattached.Snapshot.Entries, entry => entry.Role == ConversationRole.User && entry.Text == "hi");
        var answer = reattached.Snapshot.Entries[^1];
        Assert.Equal(ConversationRole.Assistant, answer.Role);
        Assert.Equal(EntryStatus.Completed, answer.Status);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
    }

    [Fact]
    public async Task Update_with_stale_referent_revision_uses_current_registration_revision()
    {
        var harness = await TriggerScheduleRuntimeTests.StartHarnessForContinuationAsync();
        await using (var runtime = harness.Runtime)
        {
            Assert.True(await runtime.SubmitUserTextAsync("schedule Hello at 8:49 Vietnam time"));
            await runtime.WaitUntilIdleAsync();
        }

        var owner = new TriggerOwner(InstanceId, ProfileId);
        var created = Assert.Single(await harness.Store.ListAsync(owner, null));
        var store = harness.Store;
        var registrations = new AutomationService(
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00b4-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf13")]),
            harness.Time);
        var definition = (await new ScenarioDefinitionStore(
            TriggerScheduleRuntimeTests.FindAgentsDirectory(),
            SyntheticProviderAliases.Default).GetAsync("general-assistant", 8))!;

        var registration = (await store.GetAsync(owner, created.AutomationId))!;
        await store.UpdateAsync(
            owner,
            registration.AutomationId,
            registration.Revision,
            "Hello (revised)",
            registration.Schedule,
            registration.NextOccurrenceAtUtc,
            registration.ExpiresAtUtc,
            harness.Time.GetUtcNow());
        var advanced = (await store.GetAsync(owner, registration.AutomationId))!;
        Assert.True(advanced.Revision > registration.Revision);

        var staleContext = ScheduleConversationContext.FromRegistration(registration, TriggerCommandAction.Create);
        var context = new TriggerCommandContext(
            owner,
            harness.Runtime.SessionId,
            "Asia/Ho_Chi_Minh",
            "move that to 8:50",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Update,
            false,
            null,
            Guid.NewGuid(),
            harness.Time.GetUtcNow(),
            staleContext);
        using var args = JsonDocument.Parse(
            $$"""{"automationId":"{{registration.AutomationId:D}}","expectedRevision":{{registration.Revision}},"relativeDayOffset":0,"localTime":"08:50","timeZone":"Asia/Ho_Chi_Minh"}""");
        var result = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.AutomationUpdate,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", result.Text, StringComparison.Ordinal);
        var moved = (await store.GetAsync(owner, registration.AutomationId))!;
        Assert.Equal(new TimeOnly(8, 50), Assert.IsType<OneShotSchedule>(moved.Schedule).LocalTime);
    }
}
