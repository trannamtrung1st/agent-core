using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class ApplicationMessageToolOfferTests
{
    [Fact]
    public void Apply_does_not_widen_brain_authorized_tools()
    {
        var definition = Definition([ToolCatalog.WorkspaceRead, ToolCatalog.WebSearch]);
        var trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi");
        var authorized = new[]
        {
            ToolRegistry.Get(ToolCatalog.WorkspaceRead).ModelDefinition
        };

        var offered = ApplicationMessageToolOffer.Apply(
            authorized,
            intermediateMessagingAllowed: true,
            ApplicationMessageBudget.Fresh(),
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        Assert.Equal([ToolCatalog.WorkspaceRead, ToolCatalog.AppMessageSend], offered.Select(tool => tool.Name));
        Assert.DoesNotContain(ToolCatalog.WebSearch, offered.Select(tool => tool.Name));
    }

    [Fact]
    public void Apply_strips_app_message_until_intermediate_messaging_is_allowed()
    {
        var definition = Definition([ToolCatalog.WorkspaceList]);
        var trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi");
        var authorized = new[]
        {
            ToolRegistry.Get(ToolCatalog.WorkspaceList).ModelDefinition,
            ToolRegistry.Get(ToolCatalog.AppMessageSend).ModelDefinition
        };

        var gated = ApplicationMessageToolOffer.Apply(
            authorized,
            intermediateMessagingAllowed: false,
            ApplicationMessageBudget.Fresh(),
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        Assert.Equal([ToolCatalog.WorkspaceList], gated.Select(tool => tool.Name));
    }

    [Fact]
    public void Apply_omits_app_message_when_budget_is_exhausted()
    {
        var definition = Definition([ToolCatalog.WorkspaceList]);
        var trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi");
        var authorized = new[] { ToolRegistry.Get(ToolCatalog.WorkspaceList).ModelDefinition };
        var exhausted = new ApplicationMessageBudget(12, 0, ApplicationMessagePolicy.Default);

        var offered = ApplicationMessageToolOffer.Apply(
            authorized,
            intermediateMessagingAllowed: true,
            exhausted,
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        Assert.Equal([ToolCatalog.WorkspaceList], offered.Select(tool => tool.Name));
    }

    [Fact]
    public void Apply_description_includes_remaining_message_count()
    {
        var definition = Definition([ToolCatalog.WorkspaceList]);
        var trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi");
        var authorized = new[] { ToolRegistry.Get(ToolCatalog.WorkspaceList).ModelDefinition };
        var budget = new ApplicationMessageBudget(3, 100, ApplicationMessagePolicy.Default);

        var offered = ApplicationMessageToolOffer.Apply(
            authorized,
            intermediateMessagingAllowed: true,
            budget,
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        var appMessage = Assert.Single(offered, tool => tool.Name == ToolCatalog.AppMessageSend);
        Assert.Contains("9 intermediate messages remain", appMessage.Description, StringComparison.Ordinal);
        Assert.Contains("7900 aggregate characters remain", appMessage.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_schema_maxLength_matches_remaining_aggregate_characters()
    {
        var definition = Definition([ToolCatalog.WorkspaceList]);
        var trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hi");
        var authorized = new[] { ToolRegistry.Get(ToolCatalog.WorkspaceList).ModelDefinition };
        var budget = new ApplicationMessageBudget(0, 7880, ApplicationMessagePolicy.Default);

        var offered = ApplicationMessageToolOffer.Apply(
            authorized,
            intermediateMessagingAllowed: true,
            budget,
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        var parameters = Assert.Single(offered, tool => tool.Name == ToolCatalog.AppMessageSend).ParametersJson;
        Assert.Contains("\"maxLength\":120", parameters, StringComparison.Ordinal);
    }

    private static AgentDefinition Definition(IReadOnlyList<string> tools) =>
        new(
            1,
            "test",
            1,
            new AgentIdentity("T", "R", "d", "t"),
            [],
            "i",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 256),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist: tools));
}
