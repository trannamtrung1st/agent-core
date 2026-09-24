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
        var definition = await LoadAsync(9);
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
        var definition = await LoadAsync(9);
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
