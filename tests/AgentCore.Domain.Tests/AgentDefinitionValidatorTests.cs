using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class AgentDefinitionValidatorTests
{
    [Fact]
    public void Rejects_non_positive_version()
    {
        var definition = new AgentDefinition(
            1,
            "examiner",
            0,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(definition));
        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
