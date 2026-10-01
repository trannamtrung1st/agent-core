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
            definition,
            trigger,
            ToolConfigurationGates.AllowAll,
            modelSupportsTools: true)!;

        Assert.Equal([ToolCatalog.WorkspaceList], gated.Select(tool => tool.Name));
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
