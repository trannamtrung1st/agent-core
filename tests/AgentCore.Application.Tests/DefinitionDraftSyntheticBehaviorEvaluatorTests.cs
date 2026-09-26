using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Application.Tests;

public sealed class DefinitionDraftSyntheticBehaviorEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_uses_offline_scripted_model_without_profile_resolver()
    {
        var evaluator = new SyntheticDefinitionDraftBehaviorEvaluator(
            new DefinitionDraftSyntheticOfflineLanguageModel(),
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            Environment = RoleEnvironment.Empty with { ToolAllowlist = [ToolCatalog.KnowledgeRetrieve] }
        };
        var scenario = new DefinitionEvaluationScenario(
            Guid.NewGuid(),
            "tool-offered",
            1,
            "Offered",
            "Please run the definition evaluation tool check.",
            DefinitionEvaluationRequirementLevel.Advisory,
            DefinitionEvaluationCheckType.ToolOffered,
            ToolCatalog.KnowledgeRetrieve,
            DateTimeOffset.UtcNow);

        var result = await evaluator.EvaluateAsync(candidate, [], scenario, CancellationToken.None);
        Assert.True(result.PromptIncludedInRequest);
        Assert.Contains(
            result.ToolObservations,
            item => string.Equals(item.ToolName, ToolCatalog.KnowledgeRetrieve, StringComparison.Ordinal));
        Assert.StartsWith("synthetic-offline/scripted/", result.ModelSelection, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_profile_registers_offline_evaluator_not_profile_resolver()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddAgentCoreInfrastructure(
            ResolveAgentDirectory(),
            profile: "Real",
            languageModel: new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "https://example.invalid",
                ApiKey = "test-key"
            });
        using var provider = services.BuildServiceProvider();
        var evaluator = provider.GetRequiredService<IDefinitionDraftSyntheticBehaviorEvaluator>();
        Assert.IsType<SyntheticDefinitionDraftBehaviorEvaluator>(evaluator);
        Assert.NotNull(provider.GetRequiredService<DefinitionDraftSyntheticOfflineLanguageModel>());
    }

    private static string ResolveAgentDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "agents");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("agents directory was not found.");
    }
}
