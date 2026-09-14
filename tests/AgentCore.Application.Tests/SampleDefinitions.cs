using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

internal static class SampleDefinitions
{
    public static AgentDefinition Examiner { get; } = new(
        1,
        "examiner",
        1,
        new AgentIdentity("Alex", "Speaking examiner", "Practice a speaking examination.", "Calm, formal and patient"),
        ["Conduct a realistic practice speaking examination"],
        "You are Alex, a practice examiner.",
        new BehaviorPolicy("acknowledgeThenContinue", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(true, "default", 1.0),
        new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
        new Dictionary<string, string> { ["scenario"] = "practice-exam" });
}
