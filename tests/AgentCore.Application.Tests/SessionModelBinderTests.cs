using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Application.Tests;

public sealed class SessionModelBinderTests
{
    [Theory]
    [InlineData(false, ModelInferenceTransport.ChatCompletions)]
    [InlineData(true, (ModelInferenceTransport)99)]
    public void Undeclared_or_unknown_hierarchy_route_is_ineligible_for_chat_and_background(bool supported, ModelInferenceTransport transport)
    {
        var descriptor = TestModelCatalogs.Synthetic().Models[0] with { DeveloperRoleSupport = supported, Transport = transport };
        var catalog = new ConfigurationModelCatalog(descriptor.OfferingId, [descriptor]);
        var error = Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(catalog));
        Assert.Contains("Core", error.Message);
        Assert.Contains("Runtime", error.Message);
        Assert.Throws<AgentCoreException>(() => ExecutionModelPolicy.RequireSelectable(catalog, descriptor.OfferingId, null));
        Assert.False(ExecutionModelPolicy.Matches(catalog, new(descriptor.OfferingId, descriptor.ConnectionId, descriptor.ExternalModelId,
            descriptor.DefaultReasoningEffort, AgentCore.Domain.Triggers.ExecutionModelSource.ConversationDefault), out _));
    }

    [Fact]
    public void Default_refresh_preserves_explicit_reasoning_effort_but_inherits_unspecified_effort()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var explicitEffort = SessionModelBinder.Bind(catalog, null, "high", ModelSelectionSource.User);
        Assert.True(explicitEffort.HasExplicitReasoningEffort);
        Assert.Equal("high", SessionModelBinder.RefreshDefault(catalog, SampleDefinitions.Examiner with { ModelDefaults = new("dca30000-0000-4000-8000-000000000001", "low") }, explicitEffort).ReasoningEffort);
        var inherited = SessionModelBinder.PinDefault(catalog);
        Assert.False(inherited.HasExplicitReasoningEffort);
        Assert.Equal("low", SessionModelBinder.RefreshDefault(catalog, SampleDefinitions.Examiner with { ModelDefaults = new("dca30000-0000-4000-8000-000000000001", "low") }, inherited).ReasoningEffort);
    }

    [Fact]
    public void Default_resolves_the_explicit_catalog_default()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var selection = SessionModelBinder.Bind(
            catalog,
            requestedOfferingId: null,
            requestedEffort: null,
            ModelSelectionSource.User);
        Assert.Equal("dca30000-0000-4000-8000-000000000001", selection.OfferingId);
        Assert.Equal("dca20000-0000-4000-8000-000000000001", selection.ConnectionId);
        Assert.Equal("dca30000-0000-4000-8000-000000000001", selection.ExternalModelId);
        Assert.Equal(ModelSelectionSource.TenantDefault, selection.SelectionSource);
        Assert.Equal("medium", selection.ReasoningEffort);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("primary-llm")]
    [InlineData("scripted-alpha")]
    [InlineData("")]
    public void Retired_request_tokens_are_not_interpreted_as_inheritance(string token)
    {
        var catalog = TestModelCatalogs.Synthetic();
        Assert.Throws<AgentCoreException>(() => SessionModelBinder.Bind(catalog, token, "high", ModelSelectionSource.User));
    }

    [Fact]
    public void Explicit_model_keeps_the_caller_source()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var user = SessionModelBinder.Bind(catalog, "dca30000-0000-4000-8000-000000000002", null, ModelSelectionSource.User);
        Assert.Equal("dca30000-0000-4000-8000-000000000002", user.OfferingId);
        Assert.Equal(ModelSelectionSource.User, user.SelectionSource);
        Assert.Null(user.ReasoningEffort);

        var host = SessionModelBinder.Bind(catalog, "dca30000-0000-4000-8000-000000000001", "low", ModelSelectionSource.Host);
        Assert.Equal(ModelSelectionSource.Host, host.SelectionSource);
        Assert.Equal("low", host.ReasoningEffort);
    }

    [Fact]
    public void Agent_default_wins_when_caller_asks_for_default()
    {
        var catalog = TestModelCatalogs.Synthetic();
        var selection = SessionModelBinder.Bind(
            catalog,
            requestedOfferingId: null,
            requestedEffort: null,
            ModelSelectionSource.User,
            new AgentModelDefaults("dca30000-0000-4000-8000-000000000002", null));
        Assert.Equal("dca30000-0000-4000-8000-000000000002", selection.OfferingId);
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
            SessionModelBinder.Bind(catalog, "dca30000-0000-4000-8000-000000000001", "ultra", ModelSelectionSource.User));
        Assert.Equal("ValidationError", unknown.Code);

        var unsupported = Assert.Throws<AgentCoreException>(() =>
            SessionModelBinder.Bind(catalog, "dca30000-0000-4000-8000-000000000002", "medium", ModelSelectionSource.User));
        Assert.Equal("ValidationError", unsupported.Code);
    }
}

internal static class TestModelCatalogs
{
    public static IModelCatalog Synthetic() =>
        new ConfigurationModelCatalog(
            "dca30000-0000-4000-8000-000000000001",
            [
                new ModelDescriptor(
                    "dca30000-0000-4000-8000-000000000001",
                    "Scripted Alpha",
                    "dca20000-0000-4000-8000-000000000001",
                    "dca30000-0000-4000-8000-000000000001",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: true,
                    ["low", "medium", "high"],
                    "medium", DeveloperRoleSupport: true),
                new ModelDescriptor(
                    "dca30000-0000-4000-8000-000000000002",
                    "Scripted Beta",
                    "dca20000-0000-4000-8000-000000000001",
                    "dca30000-0000-4000-8000-000000000002",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: false,
                    [],
                    null, DeveloperRoleSupport: true)
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
                    "dca20000-0000-4000-8000-000000000001",
                    "deepseek/deepseek-v4.1-flash",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: true,
                    ["low", "medium", "high"],
                    "medium", DeveloperRoleSupport: true),
                new ModelDescriptor(
                    "gpt-4o-mini-2024-07-18",
                    "GPT-4o mini 2024-07-18",
                    "dca20000-0000-4000-8000-000000000001",
                    "openai/gpt-4o-mini-2024-07-18",
                    Tools: true,
                    Vision: true,
                    StructuredOutput: true,
                    Reasoning: false,
                    [],
                    null, DeveloperRoleSupport: true)
            ]);
}
