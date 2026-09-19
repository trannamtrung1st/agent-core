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
    public void Real_catalog_includes_previous_working_models_without_changing_the_default()
    {
        var catalog = ModelCatalogFactory.Create(
            "Real",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                DefaultModel = "deepseek/deepseek-v4.1-flash",
                ReasoningEffort = "medium",
                Tools = true,
                Vision = false
            },
            configuration: null);
        Assert.Equal("deepseek-v41-flash", catalog.DefaultKey);
        Assert.Equal(
            ["deepseek-v41-flash", "gpt-4o-mini-2024-07-18", "openrouter-free"],
            catalog.Models.Select(model => model.Key).ToArray());
        Assert.Equal("openai/gpt-4o-mini-2024-07-18", catalog.Get("gpt-4o-mini-2024-07-18")!.ModelId);
        Assert.False(catalog.Get("gpt-4o-mini-2024-07-18")!.Reasoning);
        Assert.True(catalog.Get("gpt-4o-mini-2024-07-18")!.Tools);
        Assert.Equal("openrouter/free", catalog.Get("openrouter-free")!.ModelId);
        Assert.False(catalog.Get("openrouter-free")!.Reasoning);
        Assert.True(catalog.Get("openrouter-free")!.Tools);
    }

    [Fact]
    public void Real_profile_ignores_synthetic_appsettings_catalog()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:ModelCatalog:DefaultKey"] = "scripted-alpha",
                ["Providers:ModelCatalog:Models:0:Key"] = "scripted-alpha",
                ["Providers:ModelCatalog:Models:0:ModelId"] = "scripted-alpha",
                ["Providers:ModelCatalog:Models:1:Key"] = "scripted-beta",
                ["Providers:ModelCatalog:Models:1:ModelId"] = "scripted-beta"
            })
            .Build();
        var catalog = ModelCatalogFactory.Create(
            "Real",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                DefaultModel = "deepseek/deepseek-v4.1-flash"
            },
            configuration);
        Assert.Equal("deepseek-v41-flash", catalog.DefaultKey);
        Assert.Contains(catalog.Models, model => model.Key == "gpt-4o-mini-2024-07-18");
        Assert.DoesNotContain(catalog.Models, model => model.Key == "scripted-alpha");
    }

    [Fact]
    public void Shipped_real_catalog_sources_list_the_same_models()
    {
        var root = FindRepoRoot();
        var catalog = ModelCatalogFactory.Real();
        var launch = File.ReadAllText(Path.Combine(root, "src/AgentCore.Api/Properties/launchSettings.json"));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.real.yml"));
        foreach (var model in catalog.Models)
        {
            Assert.Contains(model.Key, launch, StringComparison.Ordinal);
            Assert.Contains(model.ModelId, launch, StringComparison.Ordinal);
            Assert.Contains(model.Key, compose, StringComparison.Ordinal);
            Assert.Contains(model.ModelId, compose, StringComparison.Ordinal);
        }

        Assert.Contains("\"Providers__ModelCatalog__DefaultKey\": \"deepseek-v41-flash\"", launch, StringComparison.Ordinal);
        Assert.Contains("Providers__ModelCatalog__DefaultKey: deepseek-v41-flash", compose, StringComparison.Ordinal);
        Assert.Contains("Providers__ModelCatalog__Models__0__ModelId: deepseek/deepseek-v4.1-flash", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Models__0__ModelId: ${AGENTCORE_LLM_MODEL", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Models__1__ModelId: ${AGENTCORE_LLM_MODEL", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Models__2__ModelId: ${AGENTCORE_LLM_MODEL", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_primary_matching_a_catalog_option_retargets_default_without_rewriting_deepseek()
    {
        var catalog = ModelCatalogFactory.Create(
            "Real",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                DefaultModel = ModelCatalogFactory.Gpt4oMini20240718ModelId
            },
            RealCatalogConfiguration());
        Assert.Equal(ModelCatalogFactory.Gpt4oMini20240718Key, catalog.DefaultKey);
        Assert.Equal(ModelCatalogFactory.DeepSeekV41FlashModelId, catalog.Get(ModelCatalogFactory.DeepSeekV41FlashKey)!.ModelId);
        Assert.Equal(ModelCatalogFactory.Gpt4oMini20240718ModelId, catalog.Default.ModelId);
    }

    [Fact]
    public void Real_duplicate_model_id_prefers_the_non_default_catalog_entry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(RealCatalogEntries())
            {
                ["Providers:ModelCatalog:Models:0:ModelId"] = ModelCatalogFactory.Gpt4oMini20240718ModelId
            })
            .Build();
        var catalog = ModelCatalogFactory.Create(
            "Real",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                DefaultModel = ModelCatalogFactory.Gpt4oMini20240718ModelId
            },
            configuration);
        Assert.Equal(ModelCatalogFactory.Gpt4oMini20240718Key, catalog.DefaultKey);
        Assert.Equal(ModelCatalogFactory.Gpt4oMini20240718ModelId, catalog.Get(ModelCatalogFactory.DeepSeekV41FlashKey)!.ModelId);
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
    public void Unsupported_provider_alias_fails_at_catalog_build_time()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:ModelCatalog:DefaultKey"] = "other",
                ["Providers:ModelCatalog:Models:0:Key"] = "other",
                ["Providers:ModelCatalog:Models:0:ProviderAlias"] = "secondary-llm",
                ["Providers:ModelCatalog:Models:0:ModelId"] = "other-model"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelCatalogFactory.Create("Synthetic", new LanguageModelProviderOptions { Adapter = "Scripted" }, configuration));
        Assert.Contains("secondary-llm", error.Message, StringComparison.Ordinal);
        Assert.Contains("primary-llm", error.Message, StringComparison.Ordinal);
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

    private static IConfiguration RealCatalogConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(RealCatalogEntries()).Build();

    private static Dictionary<string, string?> RealCatalogEntries() =>
        new()
        {
            ["Providers:ModelCatalog:DefaultKey"] = ModelCatalogFactory.DeepSeekV41FlashKey,
            ["Providers:ModelCatalog:Models:0:Key"] = ModelCatalogFactory.DeepSeekV41FlashKey,
            ["Providers:ModelCatalog:Models:0:DisplayName"] = "DeepSeek V4.1 Flash",
            ["Providers:ModelCatalog:Models:0:ProviderAlias"] = "primary-llm",
            ["Providers:ModelCatalog:Models:0:ModelId"] = ModelCatalogFactory.DeepSeekV41FlashModelId,
            ["Providers:ModelCatalog:Models:0:Tools"] = "true",
            ["Providers:ModelCatalog:Models:0:Vision"] = "false",
            ["Providers:ModelCatalog:Models:0:StructuredOutput"] = "false",
            ["Providers:ModelCatalog:Models:0:Reasoning"] = "true",
            ["Providers:ModelCatalog:Models:0:SupportedReasoningEfforts:0"] = "low",
            ["Providers:ModelCatalog:Models:0:SupportedReasoningEfforts:1"] = "medium",
            ["Providers:ModelCatalog:Models:0:SupportedReasoningEfforts:2"] = "high",
            ["Providers:ModelCatalog:Models:0:DefaultReasoningEffort"] = "medium",
            ["Providers:ModelCatalog:Models:1:Key"] = ModelCatalogFactory.Gpt4oMini20240718Key,
            ["Providers:ModelCatalog:Models:1:DisplayName"] = "GPT-4o mini 2024-07-18",
            ["Providers:ModelCatalog:Models:1:ProviderAlias"] = "primary-llm",
            ["Providers:ModelCatalog:Models:1:ModelId"] = ModelCatalogFactory.Gpt4oMini20240718ModelId,
            ["Providers:ModelCatalog:Models:1:Tools"] = "true",
            ["Providers:ModelCatalog:Models:1:Vision"] = "true",
            ["Providers:ModelCatalog:Models:1:StructuredOutput"] = "true",
            ["Providers:ModelCatalog:Models:1:Reasoning"] = "false",
            ["Providers:ModelCatalog:Models:2:Key"] = ModelCatalogFactory.OpenRouterFreeKey,
            ["Providers:ModelCatalog:Models:2:DisplayName"] = "OpenRouter Free",
            ["Providers:ModelCatalog:Models:2:ProviderAlias"] = "primary-llm",
            ["Providers:ModelCatalog:Models:2:ModelId"] = ModelCatalogFactory.OpenRouterFreeModelId,
            ["Providers:ModelCatalog:Models:2:Tools"] = "true",
            ["Providers:ModelCatalog:Models:2:Vision"] = "false",
            ["Providers:ModelCatalog:Models:2:StructuredOutput"] = "false",
            ["Providers:ModelCatalog:Models:2:Reasoning"] = "false",
            ["Providers:ModelCatalog:Models:2:CostCategory"] = "free"
        };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
