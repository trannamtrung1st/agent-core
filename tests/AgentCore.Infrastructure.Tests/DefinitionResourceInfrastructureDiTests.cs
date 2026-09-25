using AgentCore.Application.Ports;
using AgentCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class DefinitionResourceInfrastructureDiTests
{
    [Fact]
    public void InMemory_infrastructure_resolves_definition_catalog_without_deadlock()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        using var provider = services.BuildServiceProvider();
        var definitions = provider.GetRequiredService<IAgentDefinitionStore>();
        var resources = provider.GetRequiredService<IAgentDefinitionResourceAdminStore>();
        var content = provider.GetRequiredService<IDefinitionResourceContentStore>();
        Assert.NotNull(definitions);
        Assert.NotNull(resources);
        Assert.NotNull(content);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return Path.Combine(dir.FullName, "agents");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
