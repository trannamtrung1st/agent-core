using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class HostedTriggerToolTests
{
    [Fact]
    public async Task Host_schedule_tools_use_the_registered_trigger_service()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        await using var provider = services.BuildServiceProvider();
        var definitions = provider.GetRequiredService<IAgentDefinitionStore>();
        var definition = await definitions.GetAsync("general-assistant", 8);
        Assert.NotNull(definition);
        var tools = provider.GetRequiredService<SessionToolExecutor>();
        var instanceId = Guid.Parse("019944af-00c5-7000-8000-0000000000a1");
        var profileId = Guid.Parse("019944af-00c5-7000-8000-0000000000b1");
        var instances = provider.GetRequiredService<IAgentInstanceStore>();
        var memory = provider.GetRequiredService<IMemoryStore>();
        var now = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            definition!.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: false));
        await memory.SaveProfileAsync(
            new UserProfile(profileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, now)
            }, now),
            0);
        var sessionId = Guid.Parse("019944af-00c5-7000-8000-0000000000c1");
        var context = new TriggerCommandContext(
            new TriggerOwner(instanceId, profileId),
            sessionId,
            "UTC",
            "Remind me tomorrow at 9 AM to call John.",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            null,
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));

        var result = await tools.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("create", ToolCatalog.TriggerScheduleOnce, """{"intent":"Call John","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: context);

        Assert.Contains("\"status\":\"Active\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
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
}
