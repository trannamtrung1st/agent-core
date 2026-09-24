using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerScheduleSemanticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scheduled_occurrence_speak_request_has_no_tools()
    {
        var definition = await LoadAsync(9);
        var brain = new DefaultAgentBrain(new PromptContextBuilder());
        var evidence = """{"registrationId":"019944af-00f1-7000-8000-000000000001","intent":"check the oven","scheduleKind":"OneShot","scheduledAtUtc":1}""";
        var context = new AgentContext(
            definition,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.ScheduledOccurrence, evidence),
            UtcNow: Now);
        var decision = await brain.DecideAsync(context, Guid.NewGuid());
        var speak = Assert.IsType<Speak>(decision);
        Assert.Null(speak.Request.Tools);
        Assert.Contains("Scheduled reminder delivery mode", speak.Request.Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recurring_below_minimum_returns_recurrence_below_minimum_with_draft()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var context = new TriggerCommandContext(
            owner,
            Guid.NewGuid(),
            "UTC",
            "every 30s say hello to me",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.NewGuid(),
            Now);
        using var args = JsonDocument.Parse(
            """{"intent":"Say hello to me","kind":"fixed_interval","intervalSeconds":30}""");
        var result = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.Contains("\"error\":\"recurrence_below_minimum\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"scheduleDraft\"", result.Text, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(owner, null));
    }

    [Fact]
    public async Task Every_minute_correction_uses_draft_intent_and_creates_fixed_interval()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var context = new TriggerCommandContext(
            owner,
            Guid.NewGuid(),
            "UTC",
            "every minute",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.NewGuid(),
            Now,
            ScheduleDraft: draft);
        using var args = JsonDocument.Parse("""{"kind":"fixed_interval","intervalSeconds":60}""");
        var result = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", result.Text, StringComparison.Ordinal);
        var created = Assert.Single(await store.ListAsync(owner, null));
        var schedule = Assert.IsType<FixedIntervalSchedule>(created.Schedule);
        Assert.Equal(60, schedule.IntervalSeconds);
        Assert.Equal("Say hello to me", created.Intent);
    }

    [Fact]
    public async Task Heuristic_authorizer_allows_every_minute_with_active_draft()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection("Say hello to me", 30, "recurrence_below_minimum", Now);
        var decision = await new HeuristicTriggerCommandAuthorizer().AuthorizeCurrentTurnAsync(
            "every minute",
            "en",
            TriggerCommandAction.Create,
            scheduleDraft: draft);
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow, decision);
    }

    [Fact]
    public async Task Fixed_interval_update_changes_interval_max_occurrences_and_cancels()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var createContext = AuthorizedContext(owner, "every minute say hello to me", TriggerCommandAction.Create);
        using var createArgs = JsonDocument.Parse(
            """{"intent":"Say hello to me","kind":"fixed_interval","intervalSeconds":60}""");
        var createdJson = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            createArgs.RootElement,
            createContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", createdJson.Text, StringComparison.Ordinal);
        using var createdDoc = JsonDocument.Parse(createdJson.Text);
        var registrationId = createdDoc.RootElement.GetProperty("registrationId").GetString();
        var revision = createdDoc.RootElement.GetProperty("revision").GetInt64();
        var schedule = Assert.IsType<FixedIntervalSchedule>(
            (await store.ListAsync(owner, null)).Single().Schedule);
        Assert.Equal(60, schedule.IntervalSeconds);

        var registration = (await store.ListAsync(owner, null)).Single();
        var updateContext = AuthorizedContext(
            owner,
            "make that every 2 minutes",
            TriggerCommandAction.Update,
            ScheduleConversationContext.FromRegistration(registration, TriggerCommandAction.Create));
        using var updateArgs = JsonDocument.Parse(
            $$"""{"registrationId":"{{registrationId}}","expectedRevision":{{revision}},"intervalSeconds":120}""");
        var updatedJson = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerUpdate,
            updateArgs.RootElement,
            updateContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", updatedJson.Text, StringComparison.Ordinal);
        schedule = Assert.IsType<FixedIntervalSchedule>((await store.ListAsync(owner, null)).Single().Schedule);
        Assert.Equal(120, schedule.IntervalSeconds);

        using var updatedDoc = JsonDocument.Parse(updatedJson.Text);
        revision = updatedDoc.RootElement.GetProperty("revision").GetInt64();
        registration = (await store.ListAsync(owner, null)).Single();
        var capContext = AuthorizedContext(
            owner,
            "stop after 10 occurrences",
            TriggerCommandAction.Update,
            ScheduleConversationContext.FromRegistration(registration, TriggerCommandAction.Update));
        using var capArgs = JsonDocument.Parse(
            $$"""{"registrationId":"{{registrationId}}","expectedRevision":{{revision}},"maxOccurrences":10}""");
        var cappedJson = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerUpdate,
            capArgs.RootElement,
            capContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", cappedJson.Text, StringComparison.Ordinal);
        schedule = Assert.IsType<FixedIntervalSchedule>((await store.ListAsync(owner, null)).Single().Schedule);
        Assert.Equal(10, schedule.MaxOccurrences);

        using var cappedDoc = JsonDocument.Parse(cappedJson.Text);
        revision = cappedDoc.RootElement.GetProperty("revision").GetInt64();
        registration = (await store.ListAsync(owner, null)).Single();
        var cancelContext = AuthorizedContext(
            owner,
            "cancel that",
            TriggerCommandAction.Cancel,
            ScheduleConversationContext.FromRegistration(registration, TriggerCommandAction.Update));
        using var cancelArgs = JsonDocument.Parse(
            $$"""{"registrationId":"{{registrationId}}","expectedRevision":{{revision}}}""");
        var cancelledJson = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerCancel,
            cancelArgs.RootElement,
            cancelContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", cancelledJson.Text, StringComparison.Ordinal);
        Assert.Equal(TriggerRegistrationStatus.Cancelled, (await store.ListAsync(owner, null)).Single().Status);
    }

    [Fact]
    public async Task Invalid_endAtUtc_returns_schedule_validation_failed()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var context = AuthorizedContext(owner, "every minute say hello to me", TriggerCommandAction.Create);
        using var args = JsonDocument.Parse(
            """{"intent":"Say hello","kind":"fixed_interval","intervalSeconds":60,"endAtUtc":"not-a-date"}""");
        var result = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            args.RootElement,
            context,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.Contains("\"error\":\"schedule_validation_failed\"", result.Text, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(owner, null));
    }

    [Fact]
    public async Task Complete_new_request_after_rejection_does_not_merge_stale_draft_intent()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var rejectedContext = AuthorizedContext(owner, "every 30s say hello to me", TriggerCommandAction.Create);
        using var rejectedArgs = JsonDocument.Parse(
            """{"intent":"Say hello to me","kind":"fixed_interval","intervalSeconds":30}""");
        var rejected = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            rejectedArgs.RootElement,
            rejectedContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.Contains("\"scheduleDraft\"", rejected.Text, StringComparison.Ordinal);

        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var eligible = true;
        Assert.Null(ScheduleDraftAdmission.PrepareDraftForUserTurn(
            draft,
            ref eligible,
            "every minute remind me to check the oven",
            "en",
            null));

        using var intervalOnlyArgs = JsonDocument.Parse("""{"kind":"fixed_interval","intervalSeconds":60}""");
        var staleDraftContext = new TriggerCommandContext(
            owner,
            Guid.NewGuid(),
            "UTC",
            "every minute remind me to check the oven",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            Guid.NewGuid(),
            Now,
            ScheduleDraft: draft);
        var inherited = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            intervalOnlyArgs.RootElement,
            staleDraftContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", inherited.Text, StringComparison.Ordinal);
        Assert.Equal("Say hello to me", (await store.ListAsync(owner, null)).Single().Intent);

        await registrations.CancelAsync(
            owner,
            (await store.ListAsync(owner, null)).Single().RegistrationId,
            (await store.ListAsync(owner, null)).Single().Revision,
            CancellationToken.None);

        var clearedContext = AuthorizedContext(
            owner,
            "every minute remind me to check the oven",
            TriggerCommandAction.Create);
        var withoutDraft = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            intervalOnlyArgs.RootElement,
            clearedContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.Contains("\"error\":\"schedule_validation_failed\"", withoutDraft.Text, StringComparison.Ordinal);

        using var explicitIntentArgs = JsonDocument.Parse(
            """{"intent":"check the oven","kind":"fixed_interval","intervalSeconds":60}""");
        var created = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            explicitIntentArgs.RootElement,
            clearedContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", created.Text, StringComparison.Ordinal);
        Assert.Equal("check the oven", (await store.ListAsync(owner, null)).Single(r => r.Status == TriggerRegistrationStatus.Active).Intent);
    }

    [Fact]
    public async Task Every_minute_after_unrelated_turn_does_not_merge_stale_draft_intent()
    {
        var definition = await LoadAsync(10);
        var store = new InMemoryTriggerStore();
        var registrations = new TriggerRegistrationService(store, Ids(), new FakeTimeProvider(Now));
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var rejectedContext = AuthorizedContext(owner, "every 30s say hello to me", TriggerCommandAction.Create);
        using var rejectedArgs = JsonDocument.Parse(
            """{"intent":"Say hello to me","kind":"fixed_interval","intervalSeconds":30}""");
        var rejected = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            rejectedArgs.RootElement,
            rejectedContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.Contains("\"scheduleDraft\"", rejected.Text, StringComparison.Ordinal);

        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var eligible = true;
        Assert.Null(ScheduleDraftAdmission.PrepareDraftForUserTurn(
            draft,
            ref eligible,
            "what is 2 + 2?",
            "en",
            null));

        var createContext = AuthorizedContext(owner, "every minute say hello to me", TriggerCommandAction.Create);
        using var createArgs = JsonDocument.Parse(
            """{"intent":"Ping","kind":"fixed_interval","intervalSeconds":60}""");
        var result = await TriggerScheduleCommands.ExecuteAsync(
            definition,
            registrations,
            ToolCatalog.TriggerScheduleRecurring,
            createArgs.RootElement,
            createContext,
            CancellationToken.None,
            new HeuristicTriggerCommandAuthorizer());
        Assert.DoesNotContain("\"error\"", result.Text, StringComparison.Ordinal);
        var created = Assert.Single(await store.ListAsync(owner, null));
        Assert.Equal("Ping", created.Intent);
    }

    [Fact]
    public async Task General_assistant_v10_enables_fixed_interval_without_mutating_v9()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var v9 = await store.GetAsync("general-assistant", 9);
        var v10 = await store.GetAsync("general-assistant", 10);
        Assert.NotNull(v9);
        Assert.NotNull(v10);
        Assert.NotNull(v9!.TriggerPolicy);
        Assert.NotNull(v10!.TriggerPolicy);
        Assert.False(v9.TriggerPolicy.AllowFixedInterval);
        Assert.True(v10.TriggerPolicy.AllowFixedInterval);
        Assert.Equal(60, v10.TriggerPolicy.MinFixedIntervalSeconds);
    }

    private static TriggerCommandContext AuthorizedContext(
        TriggerOwner owner,
        string text,
        TriggerCommandAction action,
        ScheduleConversationContext? scheduleContext = null) =>
        new(
            owner,
            Guid.NewGuid(),
            "UTC",
            text,
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            action,
            false,
            null,
            Guid.NewGuid(),
            Now,
            scheduleContext);

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

    private static DeterministicIdGenerator Ids() =>
        new(
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00f5-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf15")]);
}
