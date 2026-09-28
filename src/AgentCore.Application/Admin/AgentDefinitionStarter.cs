using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AgentDefinitionStarter
{
    public static AgentDefinitionCandidate Create(string definitionId, ProviderAliasSet aliases)
    {
        var languageModel = ChooseLanguageModel(aliases);
        return new AgentDefinitionCandidate(
            1,
            definitionId,
            new AgentIdentity(definitionId, "Assistant", "A new agent definition.", "Clear"),
            ["Help the user"],
            "You are a new agent. Follow the operator's instructions.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences(languageModel, null, null),
            new Dictionary<string, string>());
    }

    private static string ChooseLanguageModel(ProviderAliasSet aliases)
    {
        if (aliases.LanguageModels.Contains("primary-llm"))
        {
            return "primary-llm";
        }

        var configured = aliases.LanguageModels
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        if (configured is null)
        {
            throw AgentCoreErrors.Validation("No language model alias is configured for a new definition.");
        }

        return configured;
    }
}
