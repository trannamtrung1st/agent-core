using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerScheduleTimeResolutionTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00d1-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 17, 17, 0, TimeSpan.Zero);

    [Fact]
    public async Task Relative_delay_seconds_schedules_from_trusted_now()
    {
        var created = await CreateOnceAsync("""{"intent":"Hello","relativeDelaySeconds":120}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddMinutes(2), schedule.AtUtc);
    }

    [Fact]
    public async Task Relative_delay_seconds_does_not_require_profile_timezone()
    {
        var created = await CreateOnceAsync(
            """{"intent":"Hello","relativeDelaySeconds":60}""",
            profileTimeZoneId: null);
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddSeconds(60), schedule.AtUtc);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public async Task At_utc_does_not_require_profile_timezone()
    {
        var created = await CreateOnceAsync(
            """{"intent":"Hello","atUtc":"2026-09-24T08:30:00Z"}""",
            profileTimeZoneId: null);
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 30, 0, TimeSpan.Zero), schedule.AtUtc);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public async Task Vietnam_time_label_resolves_wall_clock_at_0019()
    {
        var created = await CreateOnceAsync(
            """{"intent":"Hello","localDate":"2026-09-24","localTime":"00:19","timeZone":"viet nam time"}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal("Asia/Ho_Chi_Minh", schedule.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 17, 19, 0, TimeSpan.Zero), schedule.AtUtc);
    }

    [Fact]
    public async Task Scheduling_context_includes_trusted_clock()
    {
        var definition = await LoadAsync();
        var context = new AgentContext(
            definition,
            [],
            "",
            null,
            Domain.Conversation.SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "schedule"),
            UtcNow: Now);
        var text = PromptContextBuilder.BuildSchedulingContextSystem(context);
        Assert.NotNull(text);
        Assert.Contains("currentUtc=2026-09-23T17:17:00.0000000+00:00", text, StringComparison.Ordinal);
        Assert.Contains("relativeDelaySeconds", text, StringComparison.Ordinal);
        Assert.Contains("confirmation_required", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bare_yes_does_not_authorize_create_without_a_schedule_related_turn()
    {
        var authorizer = new HeuristicTriggerCommandAuthorizer();
        var decision = await authorizer.AuthorizeCurrentTurnAsync("yes", "en", TriggerCommandAction.Create);
        Assert.Equal(TriggerCommandAuthorizationDecision.Deny, decision);
        Assert.False(TriggerScheduleTurnPreflight.IsScheduleRelatedTurn("yes", "en"));
    }

    private static async Task<TriggerRegistration> CreateOnceAsync(
        string argumentsJson,
        string? profileTimeZoneId = "UTC")
    {
        var time = new FakeTimeProvider(Now);
        var store = new InMemoryTriggerStore();
        var ids = new DeterministicIdGenerator(
            [Guid.Parse("019944af-00d2-7000-8000-000000000001")],
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf20")]);
        var service = new TriggerRegistrationService(store, ids, time);
        var tools = new SessionToolExecutor(triggerRegistrations: service);
        var definition = await LoadAsync();
        var owner = new TriggerOwner(InstanceId, ProfileId);
        var context = new TriggerCommandContext(
            owner,
            Guid.Parse("019944af-00d1-7000-8000-0000000000c1"),
            profileTimeZoneId,
            "say hello to me after 1 minute",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.None,
            false,
            null,
            Guid.NewGuid(),
            Now);
        var result = await tools.ExecuteAsync(
            definition,
            context.SessionId,
            new ModelToolCall("create", ToolCatalog.TriggerScheduleOnce, argumentsJson),
            ToolLimits.MaxOutputBytes,
            triggerCommand: context);
        Assert.Contains("\"status\":\"Active\"", result.Text, StringComparison.Ordinal);
        return Assert.Single(await store.ListAsync(owner, null));
    }

    private static async Task<AgentDefinition> LoadAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                var store = new FileAgentDefinitionStore(agents, SyntheticProviderAliases.Default);
                return (await store.GetAsync("general-assistant", 8))!;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
