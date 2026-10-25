using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AgentDefinitionStarter
{
    public static AgentDefinitionCandidate Create(string definitionId, ProviderAliasSet aliases)
    {
        return new AgentDefinitionCandidate(
            2,
            definitionId,
            new AgentIdentity(definitionId, "Assistant", "A new agent definition.", "Clear"),
            ["Help the user"],
            "You are a new agent. Follow the operator's instructions.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences(null, null),
            new Dictionary<string, string>());
    }

}
