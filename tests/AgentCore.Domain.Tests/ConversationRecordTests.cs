using AgentCore.Domain.Conversation;

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
    public void Conversation_entry_model_provenance_is_optional_and_value_equal()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var left = new ConversationEntry(
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            1,
            null,
            ConversationRole.Assistant,
            "Hello",
            Guid.Parse("019944af-0000-7000-8000-000000000011"),
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            5,
            now,
            ModelProvenance: new ModelGenerationProvenance(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                "medium"));
        Assert.Equal(left, left with { });
        Assert.NotEqual(left, left with { ModelProvenance = null });
    }

    [Fact]
    public void Session_model_selection_is_a_concrete_value()
    {
        var left = new SessionModelSelection(
            "scripted-alpha",
            "primary-llm",
            "scripted-alpha",
            ModelSelectionSource.SystemDefault,
            "medium");
        Assert.Equal(left, left with { });
        Assert.NotEqual(left, left with { SelectionSource = ModelSelectionSource.User });
        Assert.NotEqual(left, left with { ReasoningEffort = "high" });
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

    [Fact]
    public void Local_profile_treats_seeded_friend_as_absent_and_keeps_supplied_names()
    {
        var seeded = new Dictionary<string, string> { ["language"] = "en", ["preferredName"] = "friend" };
        Assert.True(LocalUserProfile.InventedPreferredNameNeedsRemoval(seeded));
        var cleaned = LocalUserProfile.WithoutInventedPreferredName(seeded);
        Assert.False(cleaned.ContainsKey("preferredName"));
        Assert.Equal("en", cleaned["language"]);

        var prompt = LocalUserProfile.ForPrompt(seeded);
        Assert.False(LocalUserProfile.HasPreferredName(prompt));
        Assert.Equal("en", prompt["language"]);

        var supplied = LocalUserProfile.ForPrompt(new Dictionary<string, string> { ["preferredName"] = "Pat" });
        Assert.True(LocalUserProfile.HasPreferredName(supplied));
        Assert.Equal("Pat", supplied["preferredName"]);
    }
}
