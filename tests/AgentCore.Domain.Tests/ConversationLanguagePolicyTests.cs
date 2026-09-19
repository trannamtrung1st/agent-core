using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ConversationLanguagePolicyTests
{
    [Fact]
    public void Auto_is_recognized_case_insensitively()
    {
        Assert.True(ConversationLanguagePolicy.IsAuto("auto"));
        Assert.True(ConversationLanguagePolicy.IsAuto("AUTO"));
        Assert.False(ConversationLanguagePolicy.IsAuto("en"));
    }

    [Fact]
    public void Prompt_instruction_describes_auto_semantics()
    {
        Assert.Contains(
            "same language as the user's current turn",
            ConversationLanguagePolicy.PromptInstruction(ConversationLanguagePolicy.Auto),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_instruction_fixes_authoritative_language()
    {
        Assert.Equal("Respond in en.", ConversationLanguagePolicy.PromptInstruction("en"));
        Assert.Equal("Respond in vi-VN.", ConversationLanguagePolicy.PromptInstruction("  vi-VN  "));
    }
}
