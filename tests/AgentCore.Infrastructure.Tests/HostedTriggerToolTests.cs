using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
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
        var sessionId = Guid.Parse("019944af-00c5-7000-8000-0000000000c1");
        var context = new TriggerCommandContext(
            new TriggerOwner(
                Guid.Parse("019944af-00c5-7000-8000-0000000000a1"),
                Guid.Parse("019944af-00c5-7000-8000-0000000000b1")),
            sessionId,
            "UTC",
            TriggerAuthorizationClassification.CurrentUserTurn,
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
