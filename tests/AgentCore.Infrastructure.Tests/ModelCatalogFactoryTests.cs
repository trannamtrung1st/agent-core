using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class ModelCatalogFactoryTests
{
    [Fact]
    public void Synthetic_exposes_deterministic_fake_models()
    {
        var catalog = ModelCatalogFactory.Create("Synthetic", new LanguageModelProviderOptions { Adapter = "Scripted" }, configuration: null);
        Assert.Equal("scripted-alpha", catalog.DefaultKey);
        Assert.Equal(["scripted-alpha", "scripted-beta"], catalog.Models.Select(model => model.Key).ToArray());
        Assert.True(catalog.Get("scripted-alpha")!.Reasoning);
        Assert.Equal(["low", "medium", "high"], catalog.Get("scripted-alpha")!.SupportedReasoningEfforts);
        Assert.Equal("medium", catalog.Get("scripted-alpha")!.DefaultReasoningEffort);
        Assert.False(catalog.Get("scripted-beta")!.Reasoning);
    }

    [Fact]
    public void Real_primary_v41_maps_to_the_shipped_catalog_key()
    {
        var catalog = ModelCatalogFactory.FromPrimary(new LanguageModelProviderOptions
        {
            Adapter = "OpenAICompatible",
            DefaultModel = "deepseek/deepseek-v4.1-flash",
            ReasoningEffort = "medium",
            Tools = true,
            Vision = false
        });
        Assert.Equal("deepseek-v41-flash", catalog.DefaultKey);
        var model = catalog.Default;
        Assert.Equal("DeepSeek V4.1 Flash", model.DisplayName);
        Assert.Equal("deepseek/deepseek-v4.1-flash", model.ModelId);
        Assert.True(model.Tools);
        Assert.False(model.Vision);
        Assert.Equal("medium", model.DefaultReasoningEffort);
    }

    [Fact]
    public void Configured_catalog_wins_over_the_synthetic_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:ModelCatalog:DefaultKey"] = "deepseek-v41-flash",
                ["Providers:ModelCatalog:Models:0:Key"] = "deepseek-v41-flash",
                ["Providers:ModelCatalog:Models:0:DisplayName"] = "DeepSeek V4.1 Flash",
                ["Providers:ModelCatalog:Models:0:ProviderAlias"] = "primary-llm",
                ["Providers:ModelCatalog:Models:0:ModelId"] = "deepseek/deepseek-v4.1-flash",
                ["Providers:ModelCatalog:Models:0:Tools"] = "true",
                ["Providers:ModelCatalog:Models:0:Reasoning"] = "true",
                ["Providers:ModelCatalog:Models:0:SupportedReasoningEfforts:0"] = "low",
                ["Providers:ModelCatalog:Models:0:SupportedReasoningEfforts:1"] = "medium",
                ["Providers:ModelCatalog:Models:0:DefaultReasoningEffort"] = "medium"
            })
            .Build();
        var catalog = ModelCatalogFactory.Create(
            "Synthetic",
            new LanguageModelProviderOptions { Adapter = "Scripted" },
            configuration);
        Assert.Equal("deepseek-v41-flash", catalog.DefaultKey);
        Assert.DoesNotContain(catalog.Models, model => model.Key == "scripted-alpha");
    }

    [Fact]
    public void Resolver_does_not_mutate_singleton_provider_options()
    {
        var primary = new LanguageModelProviderOptions
        {
            Adapter = "Scripted",
            DefaultModel = "deepseek/deepseek-v4.1-flash",
            ReasoningEffort = "medium",
            Tools = true
        };
        var catalog = ModelCatalogFactory.Synthetic();
        var resolver = new LanguageModelResolver(new ServiceCollection().BuildServiceProvider(), "Synthetic", primary, catalog);
        var first = resolver.Resolve(
            new AgentCore.Domain.Conversation.SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                AgentCore.Domain.Conversation.ModelSelectionSource.User,
                "high"),
            ModelPurpose.Conversation);
        var second = resolver.Resolve(
            new AgentCore.Domain.Conversation.SessionModelSelection(
                "scripted-beta",
                "primary-llm",
                "scripted-beta",
                AgentCore.Domain.Conversation.ModelSelectionSource.User,
                null),
            ModelPurpose.Initiative);
        Assert.Same(first, resolver.Resolve(
            new AgentCore.Domain.Conversation.SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                AgentCore.Domain.Conversation.ModelSelectionSource.User,
                "low"),
            ModelPurpose.CompletionEvaluation));
        Assert.NotSame(first, second);
        Assert.Equal("deepseek/deepseek-v4.1-flash", primary.DefaultModel);
        Assert.Equal("medium", primary.ReasoningEffort);
        Assert.True(primary.Tools);
    }

    [Fact]
    public void Synthetic_resolver_reuses_the_registered_language_model()
    {
        var registered = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel();
        var services = new ServiceCollection();
        services.AddSingleton<ILanguageModel>(registered);
        var resolver = new LanguageModelResolver(
            services.BuildServiceProvider(),
            "Synthetic",
            new LanguageModelProviderOptions { Adapter = "Scripted" },
            ModelCatalogFactory.Synthetic());
        var resolved = resolver.Resolve(
            new AgentCore.Domain.Conversation.SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                AgentCore.Domain.Conversation.ModelSelectionSource.SystemDefault,
                "medium"),
            ModelPurpose.Conversation);
        Assert.Same(registered, resolved);
        Assert.Same(
            registered,
            resolver.Resolve(
                new AgentCore.Domain.Conversation.SessionModelSelection(
                    "scripted-beta",
                    "primary-llm",
                    "scripted-beta",
                    AgentCore.Domain.Conversation.ModelSelectionSource.User,
                    null),
                ModelPurpose.Initiative));
    }
}
