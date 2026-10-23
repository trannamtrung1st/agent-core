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
    public async Task Scheduled_occurrence_speak_request_projects_authorized_generic_tools()
    {
        var definition = await LoadAsync(9);
        var brain = new DefaultAgentBrain(new PromptContextBuilder());
        var evidence = """{"automationId":"019944af-00f1-7000-8000-000000000001","instructions":"check the oven","scheduleKind":"OneShot","scheduledAtUtc":1}""";
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
        Assert.Contains(speak.Request.Tools!, tool => tool.Name == ToolCatalog.KnowledgeRetrieve);
        Assert.DoesNotContain(speak.Request.Tools!, tool => ToolCatalog.IsBrowserTool(tool.Name));
        Assert.Contains(speak.Request.Messages, m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded background Session task.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task General_assistant_v10_enables_fixed_interval_without_mutating_v9()
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
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

    private static async Task<AgentDefinition> LoadAsync(int version)
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
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
}
