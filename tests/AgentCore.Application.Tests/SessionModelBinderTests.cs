using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Application.Tests;

public sealed class SessionModelBinderTests
{
    [Fact]
    public void Default_resolves_system_catalog_default()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var selection = SessionModelBinder.Bind(
            catalog,
            requestedKey: null,
            requestedEffort: null,
            ModelSelectionSource.User);
        Assert.Equal("scripted-alpha", selection.CatalogKey);
        Assert.Equal("primary-llm", selection.ProviderAlias);
        Assert.Equal("scripted-alpha", selection.ModelId);
        Assert.Equal(ModelSelectionSource.SystemDefault, selection.SelectionSource);
        Assert.Equal("medium", selection.ReasoningEffort);
    }

    [Fact]
    public void Explicit_default_key_still_pins_the_current_concrete_default()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var selection = SessionModelBinder.Bind(
            catalog,
            "default",
            "high",
            ModelSelectionSource.User);
        Assert.Equal("scripted-alpha", selection.CatalogKey);
        Assert.Equal(ModelSelectionSource.SystemDefault, selection.SelectionSource);
        Assert.Equal("high", selection.ReasoningEffort);
    }

    [Fact]
    public void Explicit_model_keeps_the_caller_source()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var user = SessionModelBinder.Bind(catalog, "scripted-beta", null, ModelSelectionSource.User);
        Assert.Equal("scripted-beta", user.CatalogKey);
        Assert.Equal(ModelSelectionSource.User, user.SelectionSource);
        Assert.Null(user.ReasoningEffort);

        var host = SessionModelBinder.Bind(catalog, "scripted-alpha", "low", ModelSelectionSource.Host);
        Assert.Equal(ModelSelectionSource.Host, host.SelectionSource);
        Assert.Equal("low", host.ReasoningEffort);
    }

    [Fact]
    public void Agent_default_wins_when_caller_asks_for_default()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var selection = SessionModelBinder.Bind(
            catalog,
            requestedKey: null,
            requestedEffort: null,
            ModelSelectionSource.User,
            new AgentModelDefaults("scripted-beta", null));
        Assert.Equal("scripted-beta", selection.CatalogKey);
        Assert.Equal(ModelSelectionSource.AgentDefault, selection.SelectionSource);
        Assert.Null(selection.ReasoningEffort);
    }

    [Fact]
    public void Unsupported_model_is_rejected()
    {
        var error = Assert.Throws<AgentCoreException>(() =>
            SessionModelBinder.Bind(
                TestModelCatalogs.Synthetic(),
                "missing-model",
                null,
                ModelSelectionSource.User));
        Assert.Equal("ValidationError", error.Code);
    }

    [Fact]
    public void Unsupported_effort_is_rejected()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var unknown = Assert.Throws<AgentCoreException>(() =>
            SessionModelBinder.Bind(catalog, "scripted-alpha", "ultra", ModelSelectionSource.User));
        Assert.Equal("ValidationError", unknown.Code);

        var unsupported = Assert.Throws<AgentCoreException>(() =>
            SessionModelBinder.Bind(catalog, "scripted-beta", "medium", ModelSelectionSource.User));
        Assert.Equal("ValidationError", unsupported.Code);
    }
}

internal static class TestModelCatalogs
{
    public static IModelCatalog Synthetic() =>
        new ConfigurationModelCatalog(
            "scripted-alpha",
            [
                new ModelDescriptor(
                    "scripted-alpha",
                    "Scripted Alpha",
                    "primary-llm",
                    "scripted-alpha",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: true,
                    ["low", "medium", "high"],
                    "medium"),
                new ModelDescriptor(
                    "scripted-beta",
                    "Scripted Beta",
                    "primary-llm",
                    "scripted-beta",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: false,
                    [],
                    null)
            ]);

    public static IModelCatalog WithDefault(string defaultKey) =>
        new ConfigurationModelCatalog(defaultKey, Synthetic().Models);

    public static IModelCatalog Real() =>
        new ConfigurationModelCatalog(
            "deepseek-v41-flash",
            [
                new ModelDescriptor(
                    "deepseek-v41-flash",
                    "DeepSeek V4.1 Flash",
                    "primary-llm",
                    "deepseek/deepseek-v4.1-flash",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: true,
                    ["low", "medium", "high"],
                    "medium"),
                new ModelDescriptor(
                    "gpt-4o-mini-2024-07-18",
                    "GPT-4o mini 2024-07-18",
                    "primary-llm",
                    "openai/gpt-4o-mini-2024-07-18",
                    Tools: true,
                    Vision: true,
                    StructuredOutput: true,
                    Reasoning: false,
                    [],
                    null)
            ]);
}
