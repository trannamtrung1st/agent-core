using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ConversationRecordTests
{
    [Fact]
    public void Conversation_entry_is_value_equality()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var left = new ConversationEntry(
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            1,
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            ConversationRole.User,
            "Hello",
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            5,
            5,
            now);
        var right = left with { };
        Assert.Equal(left, right);
        Assert.NotEqual(left, left with { Text = "Hi" });
    }

    [Fact]
    public void Agent_definition_identity_is_immutable_data()
    {
        var definition = new AgentDefinition(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "Speaking examiner", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        Assert.Equal("examiner", definition.Id);
        Assert.Equal("Alex", definition.Identity.Name);
    }

    [Fact]
    public void Local_profile_rejects_unknown_keys_and_oversized_preferences()
    {
        LocalUserProfile.Validate(new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "Pat" });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalUserProfile.Validate(new Dictionary<string, string> { ["nickname"] = "x" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalUserProfile.Validate(new Dictionary<string, string> { ["language"] = new string('a', 2001) }));
    }
}
