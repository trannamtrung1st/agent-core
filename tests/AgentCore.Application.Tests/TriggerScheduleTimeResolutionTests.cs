using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
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
    public async Task Relative_delay_seconds_ignores_a_zero_day_offset_default()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","relativeDelaySeconds":60,"relativeDayOffset":0,"localDate":"","localTime":"","atUtc":""}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddSeconds(60), schedule.AtUtc);
    }

    [Fact]
    public async Task Relative_delay_seconds_ignores_blank_time_strings()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","relativeDelaySeconds":"60","relativeDayOffset":null,"localDate":"","localTime":"","atUtc":"","timeZone":null}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddSeconds(60), schedule.AtUtc);
    }

    [Fact]
    public async Task Relative_delay_seconds_ignores_null_time_fields()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","relativeDelaySeconds":60,"relativeDayOffset":null,"localDate":null,"localTime":null,"atUtc":null,"timeZone":null}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddSeconds(60), schedule.AtUtc);
    }

    [Fact]
    public async Task Relative_delay_seconds_schedules_from_trusted_now()
    {
        var created = await CreateOnceAsync("""{"executionTarget":"backgroundSession","instructions":"Hello","relativeDelaySeconds":120}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddMinutes(2), schedule.AtUtc);
    }

    [Fact]
    public async Task Relative_delay_seconds_uses_UTC_without_a_timezone_argument()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","relativeDelaySeconds":60}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(Now.AddSeconds(60), schedule.AtUtc);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public async Task At_utc_uses_UTC_without_a_timezone_argument()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","atUtc":"2026-09-24T08:30:00Z"}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 30, 0, TimeSpan.Zero), schedule.AtUtc);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public async Task Vietnam_time_label_resolves_wall_clock_at_0019()
    {
        var created = await CreateOnceAsync(
            """{"executionTarget":"backgroundSession","instructions":"Hello","localDate":"2026-09-24","localTime":"00:19","timeZone":"viet nam time"}""");
        var schedule = Assert.IsType<OneShotSchedule>(created.Schedule);
        Assert.Equal("Asia/Ho_Chi_Minh", schedule.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 17, 19, 0, TimeSpan.Zero), schedule.AtUtc);
    }

    [Fact]
    public async Task Scheduling_context_includes_trusted_clock()
    {
        var definition = await LoadAsync();
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
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
            UtcNow: Now, AgentInstanceId: InstanceId, OwnedSessionId: session)
        { ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session,
            Guid.NewGuid(), activation, activation, null, null, Domain.Conversation.ExecutionOrigin.UserMessage) };
        context = context with { RequesterContext = new(context.ExecutionContext!, "Organization", "Requester", [], false, 1, Now) };
        Assert.Null(PromptContextBuilder.BuildSchedulingContext(context with { ExecutionContext = null }));
        var text = PromptContextBuilder.BuildSchedulingContext(context);
        Assert.NotNull(text);
        Assert.Contains("currentUtc=2026-09-23T17:17:00.0000000+00:00", text, StringComparison.Ordinal);
        var request = new PromptContextBuilder().Build(context, Guid.NewGuid());
        var clock = Assert.Single(request.Context!.Sections, section => section.Id == AgentCore.Application.Context.ContextSectionId.Scheduling);
        Assert.Equal(ModelRole.Developer, clock.Message.Role);
        Assert.Contains("currentUtc=2026-09-23T17:17:00.0000000+00:00", clock.Message.Text);
        var rules = Assert.Single(request.Context.Sections, section => section.Id == AgentCore.Application.Context.ContextSectionId.CoreScheduling);
        Assert.Equal(ModelRole.System, rules.Message.Role);
        Assert.Contains("relativeDelaySeconds", rules.Message.Text);
        Assert.Contains("qualified human decision", rules.Message.Text);
        Assert.Contains("never pre-authorize future actions", rules.Message.Text);
        Assert.DoesNotContain("relativeDelaySeconds", text);

    }

    private static async Task<TenantAutomationChange> CreateOnceAsync(string argumentsJson)
    {
        var definition = await LoadAsync();
        var ids = new DeterministicIdGenerator([Guid.Parse("019944af-00d2-7000-8000-000000000001")], []);
        return TriggerScheduleCommands.NormalizeTenant(definition, null,
            new(ToolCatalog.AutomationCreate, argumentsJson, null, 0),
            Guid.Parse("019944af-00d1-7000-8000-0000000000c1"), Now, ids);
    }

    private static async Task<AgentDefinition> LoadAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                var store = new ScenarioDefinitionStore(agents, SyntheticProviderAliases.Default);
                return (await store.GetAsync("general-assistant", 8))!;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
