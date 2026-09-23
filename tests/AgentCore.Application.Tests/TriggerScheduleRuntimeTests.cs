using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerScheduleRuntimeTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-00b1-7000-8000-0000000000a1");
    private static readonly Guid OtherInstanceId = Guid.Parse("019944af-00b1-7000-8000-0000000000a2");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00b1-7000-8000-0000000000b1");
    private static readonly Guid OtherProfileId = Guid.Parse("019944af-00b1-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Natural_one_shot_can_be_listed_moved_and_cancelled_without_approval()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        Assert.True(await runtime.SubmitUserTextAsync(
            "Remind me tomorrow at 9 AM to call John, then list my schedules, then move that schedule to 11, then cancel that schedule."));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(new TriggerOwner(InstanceId, ProfileId), null));
        Assert.Equal(TriggerRegistrationStatus.Cancelled, saved.Status);
        Assert.Equal(3, saved.Revision);
        Assert.Equal(2, saved.ScheduleRevision);
        var schedule = Assert.IsType<OneShotSchedule>(saved.Schedule);
        Assert.Equal(new TimeOnly(11, 0), schedule.LocalTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero), schedule.AtUtc);
        Assert.Equal("UTC", schedule.TimeZoneId);
        Assert.Contains("The schedule was cancelled.", runtime.Snapshot.Entries[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("approval", runtime.Snapshot.Entries[^1].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Separate_turns_create_list_move_and_cancel_without_shared_authority()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);

        Assert.True(await runtime.SubmitUserTextAsync("Remind me tomorrow at 9 AM to call John."));
        await runtime.WaitUntilIdleAsync();
        var created = Assert.Single(await harness.Store.ListAsync(owner, null));
        Assert.Equal(new TimeOnly(9, 0), Assert.IsType<OneShotSchedule>(created.Schedule).LocalTime);

        Assert.True(await runtime.SubmitUserTextAsync("What reminders do I have?"));
        await runtime.WaitUntilIdleAsync();
        Assert.Contains("You have a reminder: Call John.", runtime.Snapshot.Entries[^1].Text, StringComparison.Ordinal);
        Assert.Equal(TriggerRegistrationStatus.Active, (await harness.Store.GetAsync(owner, created.RegistrationId))!.Status);
        Assert.Equal(1, (await harness.Store.GetAsync(owner, created.RegistrationId))!.ScheduleRevision);

        Assert.True(await runtime.SubmitUserTextAsync("Move that reminder to 10."));
        await runtime.WaitUntilIdleAsync();
        var moved = (await harness.Store.GetAsync(owner, created.RegistrationId))!;
        var movedSchedule = Assert.IsType<OneShotSchedule>(moved.Schedule);
        Assert.Equal(new TimeOnly(10, 0), movedSchedule.LocalTime);
        Assert.Equal(2, moved.ScheduleRevision);
        Assert.Contains("Moved the reminder.", runtime.Snapshot.Entries[^1].Text, StringComparison.Ordinal);

        Assert.True(await runtime.SubmitUserTextAsync("Cancel that reminder."));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TriggerRegistrationStatus.Cancelled, (await harness.Store.GetAsync(owner, created.RegistrationId))!.Status);
        Assert.Contains("The schedule was cancelled.", runtime.Snapshot.Entries[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_authority_does_not_create_update_or_cancel()
    {
        var time = new FakeTimeProvider(Now);
        var store = new InMemoryTriggerStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 4).Select(index => Guid.Parse($"019944af-00b6-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf16")]);
        var service = new TriggerRegistrationService(store, ids, time);
        var tools = new SessionToolExecutor(triggerRegistrations: service);
        var enabled = await LoadAsync(8);
        var owner = new TriggerOwner(InstanceId, ProfileId);
        var created = await tools.ExecuteAsync(
            enabled,
            Guid.Parse("019944af-00b1-7000-8000-0000000000c1"),
            new ModelToolCall("create", ToolCatalog.TriggerScheduleOnce, """{"intent":"Call John","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(owner, TriggerAuthorizationClassification.CurrentUserTurn, false, null, TriggerCommandAction.Create));
        Assert.Contains("\"status\":\"Active\"", created.Text, StringComparison.Ordinal);

        var list = Context(
            owner,
            TriggerAuthorizationClassification.CurrentUserTurn,
            false,
            null,
            TriggerCommandAction.List,
            currentUserText: "What reminders do I have?");
        foreach (var name in new[] { ToolCatalog.TriggerScheduleOnce, ToolCatalog.TriggerUpdate, ToolCatalog.TriggerCancel })
        {
            var rejected = await tools.ExecuteAsync(
                enabled,
                list.SessionId,
                new ModelToolCall("mistaken", name, """{"intent":"Nope","relativeDayOffset":1,"localTime":"09:00","registrationId":"019944af-00b6-7000-8000-000000000001","expectedRevision":1}"""),
                ToolLimits.MaxOutputBytes,
                triggerCommand: list);
            Assert.Contains("current_turn_not_authorized", rejected.Text, StringComparison.Ordinal);
        }

        var listed = await tools.ExecuteAsync(
            enabled,
            list.SessionId,
            new ModelToolCall("list", ToolCatalog.TriggerList, "{}"),
            ToolLimits.MaxOutputBytes,
            triggerCommand: list);
        Assert.Contains("Call John", listed.Text, StringComparison.Ordinal);
        Assert.Equal(1, await store.CountActiveAsync(owner));
    }

    [Fact]
    public async Task Natural_monday_schedule_keeps_an_indefinite_wall_clock_recurrence()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        Assert.True(await runtime.SubmitUserTextAsync("Remind me every Monday at 9."));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(new TriggerOwner(InstanceId, ProfileId), null));
        var schedule = Assert.IsType<WeeklySchedule>(saved.Schedule);
        Assert.Equal(1, schedule.IntervalWeeks);
        Assert.Equal(DayOfWeek.Monday, Assert.Single(schedule.Weekdays));
        Assert.Null(schedule.EndDate);
        Assert.Null(schedule.MaxOccurrences);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero), saved.NextOccurrenceAtUtc);
        Assert.Contains("Scheduled the Monday call.", runtime.Snapshot.Entries[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_history_cannot_authorize_another_schedule()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        Assert.True(await runtime.SubmitUserTextAsync("Remind me tomorrow at 9 AM to call John."));
        await runtime.WaitUntilIdleAsync();
        Assert.True(await runtime.SubmitUserTextAsync($"thanks {ScriptedLanguageModel.ScheduleForceMarker}"));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(new TriggerOwner(InstanceId, ProfileId), null));
        Assert.Equal(TriggerRegistrationStatus.Active, saved.Status);
        var declined = runtime.Snapshot.Entries[^1].Text;
        Assert.True(
            declined.Contains("I did not save a schedule.", StringComparison.Ordinal)
            || declined.Contains("did not understand that schedule request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unrelated_authority_disabled_policy_and_stale_revision_do_not_mutate_schedules()
    {
        var time = new FakeTimeProvider(Now);
        var store = new InMemoryTriggerStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00b2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf11")]);
        var service = new TriggerRegistrationService(store, ids, time);
        var tools = new SessionToolExecutor(triggerRegistrations: service);
        var enabled = await LoadAsync(8);
        var owner = new TriggerOwner(InstanceId, ProfileId);
        var context = Context(
            owner,
            TriggerAuthorizationClassification.CurrentUserTurn,
            executePending: false,
            pending: null,
            TriggerCommandAction.Create | TriggerCommandAction.Update);
        var created = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("create", ToolCatalog.TriggerScheduleOnce, """{"intent":"Call John","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: context);
        Assert.Contains("\"status\":\"Active\"", created.Text, StringComparison.Ordinal);

        var initiative = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("initiative", ToolCatalog.TriggerScheduleOnce, """{"intent":"Sneaky","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(owner, TriggerAuthorizationClassification.Initiative, false, null));
        Assert.Contains("confirmation_required", initiative.Text, StringComparison.Ordinal);
        Assert.NotNull(initiative.TriggerProposal);
        Assert.Equal(1, await store.CountActiveAsync(owner));
        var environment = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("environment", ToolCatalog.TriggerScheduleOnce, """{"intent":"Environment","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(owner, TriggerAuthorizationClassification.Environment, false, null));
        Assert.Contains("confirmation_required", environment.Text, StringComparison.Ordinal);
        Assert.Equal(1, await store.CountActiveAsync(owner));

        var confirmed = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("confirm", ToolCatalog.TriggerScheduleOnce, """{"intent":"Different","relativeDayOffset":2,"localTime":"15:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(
                owner,
                TriggerAuthorizationClassification.CurrentUserTurn,
                true,
                initiative.TriggerProposal,
                TriggerCommandAction.Create));
        Assert.Contains("Sneaky", confirmed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Different", confirmed.Text, StringComparison.Ordinal);

        foreach (var classification in new[]
        {
            TriggerAuthorizationClassification.UnrelatedUserTurn,
            TriggerAuthorizationClassification.Historical,
            TriggerAuthorizationClassification.Memory,
            TriggerAuthorizationClassification.Occurrence
        })
        {
            var rejected = await tools.ExecuteAsync(
                enabled,
                context.SessionId,
                new ModelToolCall("reject", ToolCatalog.TriggerScheduleOnce, """{"intent":"Nope","relativeDayOffset":1,"localTime":"09:00"}"""),
                ToolLimits.MaxOutputBytes,
                triggerCommand: Context(owner, classification, false, null));
            Assert.Contains(
                classification == TriggerAuthorizationClassification.UnrelatedUserTurn
                    ? "current_turn_not_authorized"
                    : "forbidden",
                rejected.Text,
                StringComparison.Ordinal);
            Assert.Null(rejected.TriggerProposal);
        }

        var disabled = await tools.ExecuteAsync(
            enabled with { TriggerPolicy = null },
            context.SessionId,
            new ModelToolCall("disabled", ToolCatalog.TriggerScheduleOnce, """{"intent":"Nope","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: context);
        Assert.Contains("Scheduling is disabled", disabled.Text, StringComparison.Ordinal);

        var stale = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("stale", ToolCatalog.TriggerUpdate, """{"registrationId":"019944af-00b2-7000-8000-000000000001","expectedRevision":9,"intent":"Changed"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(
                owner,
                TriggerAuthorizationClassification.CurrentUserTurn,
                executePending: false,
                pending: null,
                TriggerCommandAction.Update,
                currentUserText: "Move that reminder to 10."));
        Assert.Contains("stale", stale.Text, StringComparison.OrdinalIgnoreCase);

        var otherOwner = await tools.ExecuteAsync(
            enabled,
            context.SessionId,
            new ModelToolCall("other", ToolCatalog.TriggerCancel, """{"registrationId":"019944af-00b2-7000-8000-000000000001","expectedRevision":1}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: Context(
                new TriggerOwner(OtherInstanceId, OtherProfileId),
                TriggerAuthorizationClassification.CurrentUserTurn,
                false,
                null,
                TriggerCommandAction.Cancel,
                currentUserText: "Cancel that reminder."));
        Assert.Contains("not_found", otherOwner.Text, StringComparison.Ordinal);
        Assert.Equal(2, await store.CountActiveAsync(owner));
        Assert.Equal("Call John", (await store.GetAsync(owner, Guid.Parse("019944af-00b2-7000-8000-000000000001")))!.Intent);
    }

    [Fact]
    public async Task Non_schedule_tools_still_reject_session_mutation_arguments()
    {
        var tools = new SessionToolExecutor();
        var definition = await LoadAsync(8);
        foreach (var name in new[] { "revision", "snapshot", "mutate", "persist", "history" })
        {
            var result = await tools.ExecuteAsync(
                definition,
                Guid.NewGuid(),
                new ModelToolCall("guard", ToolCatalog.KnowledgeRetrieve, $$"""{"identity":"support-order-policy","{{name}}":"1"}"""),
                ToolLimits.MaxOutputBytes);
            Assert.Contains("not permitted", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unrelated_user_turn_drops_a_pending_schedule_proposal()
    {
        var harness = await StartAsync(environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000001"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));
        Assert.Contains("confirm", runtime.Snapshot.Entries[^1].Text, StringComparison.OrdinalIgnoreCase);

        Assert.True(await runtime.SubmitUserTextAsync("What is the weather?"));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("yes"));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));
        var declined = runtime.Snapshot.Entries[^1].Text;
        Assert.True(
            declined.Contains("I did not save a schedule.", StringComparison.Ordinal)
            || declined.Contains("did not understand that schedule request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmation_persists_the_stored_proposal_not_the_model_replacement()
    {
        var harness = await StartAsync(environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000002"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("yes"));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(owner, null));
        Assert.Equal(TriggerRegistrationStatus.Active, saved.Status);
        Assert.Equal("Sneaky", saved.Intent);
        var schedule = Assert.IsType<OneShotSchedule>(saved.Schedule);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), schedule.AtUtc);
    }

    [Fact]
    public async Task Queued_unrelated_turn_during_proposal_creation_drops_the_proposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = await StartAsync(new HoldBeforeEnvironmentScheduleModel(started, release), environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000004"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            true,
            await runtime.SubmitPersistedUserTextAsync(
                "What is the weather?",
                Guid.Parse("019944af-00b5-7000-8000-000000000021"),
                behavior: UserTextBehavior.Queue));
        Assert.Contains(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.User && entry.Text == "What is the weather?");
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("yes"));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));
    }

    [Fact]
    public async Task Idle_speech_final_drops_a_pending_schedule_proposal()
    {
        var harness = await StartAsync(environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000005"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.Mode);

        var utterance = Guid.Parse("019944af-00b5-7000-8000-000000000011");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "What is the weather?", 0.9), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.2);
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("yes"));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));
    }

    [Fact]
    public async Task User_steer_drops_a_pending_schedule_proposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = await StartAsync(new HoldAfterEnvironmentScheduleModel(started), environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000003"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("yes"));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));
    }

    [Fact]
    public void Memory_and_old_wording_do_not_count_as_the_current_request()
    {
        const string remembered = "Remind me tomorrow at 9 to call John.";
        Assert.Equal(TriggerCommandAction.Create, Authorizer.AuthorizeCurrentTurn(remembered, "en"));
        Assert.Equal(
            TriggerAuthorizationClassification.UnrelatedUserTurn,
            TriggerAuthorization.Classify(TriggerKind.UserTurn, "thanks", null, Authorizer, "en").Classification);
        var confirmation = TriggerAuthorization.Classify(
            TriggerKind.UserTurn,
            "yes",
            new PendingTriggerProposal(ToolCatalog.TriggerScheduleOnce, "{}"),
            Authorizer,
            "en");
        Assert.Equal(TriggerAuthorizationClassification.CurrentUserTurn, confirmation.Classification);
        Assert.Equal(TriggerCommandAction.Create, confirmation.AllowedActions);
        Assert.True(Authorizer.IsScheduleConfirmation("I approve", "en"));
        Assert.Equal(
            TriggerAuthorizationClassification.Initiative,
            TriggerAuthorization.Classify(TriggerKind.LongSilence, remembered, null, Authorizer, "en").Classification);
        Assert.Equal(TriggerCommandAction.List, Authorizer.AuthorizeCurrentTurn("What reminders do I have?", "en"));
        Assert.Equal(TriggerCommandAction.Update, Authorizer.AuthorizeCurrentTurn("Move that reminder to 10.", "en"));
        Assert.Equal(TriggerCommandAction.Cancel, Authorizer.AuthorizeCurrentTurn("Cancel that reminder.", "en"));
        Assert.Equal(
            TriggerCommandAction.None,
            TriggerAuthorization.Classify(TriggerKind.UserTurn, "What reminders do I have?", null, Authorizer, "en").AllowedActions
                & TriggerCommandAction.Create);
        Assert.Equal(
            TriggerCommandAction.None,
            Authorizer.AuthorizeCurrentTurn("don't create a reminder", "en") & TriggerCommandAction.Create);
    }

    [Fact]
    public async Task Say_hello_in_one_minute_schedules_without_approval()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);

        Assert.True(await runtime.SubmitUserTextAsync("say hello to me in 1 minute"));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(owner, null));
        Assert.Equal(TriggerRegistrationStatus.Active, saved.Status);
        Assert.Contains("Hello", saved.Intent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry =>
            entry.Role == ConversationRole.Assistant
            && entry.Text.Contains("confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Initiative_proposal_persists_after_I_approve()
    {
        var harness = await StartAsync(environmentScheduling: true);
        await using var runtime = harness.Runtime;
        var owner = new TriggerOwner(InstanceId, ProfileId);
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(
            Guid.Parse("019944af-00b5-7000-8000-000000000006"),
            ScriptedLanguageModel.ScheduleForceMarker));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await harness.Store.ListAsync(owner, null));

        Assert.True(await runtime.SubmitUserTextAsync("I approve"));
        await runtime.WaitUntilIdleAsync();

        var saved = Assert.Single(await harness.Store.ListAsync(owner, null));
        Assert.Equal("Sneaky", saved.Intent);
    }

    private static readonly ITriggerCommandAuthorizer Authorizer = new HeuristicTriggerCommandAuthorizer();

    private static TriggerCommandContext Context(
        TriggerOwner owner,
        TriggerAuthorizationClassification classification,
        bool executePending,
        PendingTriggerProposal? pending,
        TriggerCommandAction actions = TriggerCommandAction.None,
        string? currentUserText = "Remind me tomorrow at 9 AM to call John.") =>
        new(
            owner,
            Guid.Parse("019944af-00b1-7000-8000-0000000000c1"),
            "UTC",
            currentUserText,
            "en",
            classification,
            actions,
            executePending,
            pending,
            Guid.Parse("019944af-00b1-7000-8000-0000000000d1"),
            Now);

    private static async Task<Harness> StartAsync(ILanguageModel? model = null, bool environmentScheduling = false)
    {
        var definition = await LoadAsync(8);
        if (environmentScheduling)
        {
            definition = definition with
            {
                InitiativePolicy = definition.InitiativePolicy with
                {
                    Enabled = true,
                    Triggers = ["environmentUpdate", "longSilence"]
                }
            };
        }
        var time = new FakeTimeProvider(Now);
        var store = new InMemoryTriggerStore();
        var sessionIds = new DeterministicIdGenerator(
            Enumerable.Range(20, 32).Select(index => Guid.Parse($"019944af-00b3-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf12")]);
        var triggerIds = new DeterministicIdGenerator(
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00b4-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf13")]);
        var registrations = new TriggerRegistrationService(store, triggerIds, time);
        var snapshot = new SessionSnapshot(
            1,
            sessionIds.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: InstanceId);
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(snapshot, 0);
        var runtime = new SessionRuntime(
            snapshot,
            model ?? new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            new CapturingSessionOutput(),
            sessionIds,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(triggerRegistrations: registrations));
        await runtime.AttachAsync();
        await runtime.ApplyProfileAsync(new UserProfile(
            ProfileId,
            1,
            new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
            },
            Now));
        return new Harness(runtime, store);
    }

    private static async Task<AgentDefinition> LoadAsync(int version)
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", version))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed record Harness(SessionRuntime Runtime, InMemoryTriggerStore Store);

    private sealed class HoldBeforeEnvironmentScheduleModel(
        TaskCompletionSource started,
        TaskCompletionSource release) : ILanguageModel
    {
        private readonly ScriptedLanguageModel _inner = new();

        public ModelCapabilities Capabilities => _inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var toolRounds = request.Messages.Count(message => message.Role == ModelRole.Tool);
            var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
            if (toolRounds == 0 && lastUser.Contains(ScriptedLanguageModel.ScheduleForceMarker, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await foreach (var item in _inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class HoldAfterEnvironmentScheduleModel(TaskCompletionSource started) : ILanguageModel
    {
        private readonly ScriptedLanguageModel _inner = new();

        public ModelCapabilities Capabilities => _inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var toolRounds = request.Messages.Count(message => message.Role == ModelRole.Tool);
            var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
            if (toolRounds > 0 && lastUser.Contains(ScriptedLanguageModel.ScheduleForceMarker, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            await foreach (var item in _inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
