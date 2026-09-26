using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Application.Tests;

internal static class AdminEvaluationTestSupport
{
    internal static AgentDefinitionDraftSyntheticEvaluationRunner CreateRunner() =>
        new(
            new SyntheticDefinitionDraftBehaviorEvaluator(
                new LanguageModelResolver(
                    BuildScriptedProvider(),
                    "Synthetic",
                    new LanguageModelProviderOptions { Adapter = "Scripted" },
                    TestModelCatalogs.Synthetic()),
                TestModelCatalogs.Synthetic(),
                ToolConfigurationGates.AllowAll),
            ToolConfigurationGates.AllowAll);

    private static ServiceProvider BuildScriptedProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILanguageModel, ScriptedLanguageModel>();
        return services.BuildServiceProvider();
    }
}
