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

    [Fact]
    public async Task Non_schedule_tools_still_reject_session_mutation_arguments()
    {
        var tools = new SessionToolExecutor();
        var definition = await LoadAsync(8);
        foreach (var name in new[] { "revision", "snapshot", "mutate", "persist", "history" })
        {
            var arguments = $$"""{"identity":"support-order-policy","{{name}}":"1"}""";
            Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.KnowledgeRetrieve, arguments));
            var result = await tools.ExecuteAsync(definition, Guid.NewGuid(),
                new ModelToolCall("guard", ToolCatalog.KnowledgeRetrieve, arguments), ToolLimits.MaxOutputBytes);
            Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<AgentDefinition> LoadAsync(int version)
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", version))!;
    }

    internal static string FindAgentsDirectory() => FindAgents();

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
