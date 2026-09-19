using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ConversationLanguagePolicyTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void Validate_accepts_auto(string language)
    {
        Assert.Equal(ConversationLanguagePolicy.Auto, ConversationLanguagePolicy.Validate(language));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    [InlineData("vi-VN")]
    [InlineData("fr-FR")]
    [InlineData("  en  ")]
    public void Validate_accepts_fixed_bcp47_like_tags(string language)
    {
        var normalized = ConversationLanguagePolicy.Validate(language);
        Assert.False(ConversationLanguagePolicy.IsAuto(normalized));
        Assert.Equal(language.Trim(), normalized, ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("potato!")]
    [InlineData("en_US")]
    [InlineData("!!!")]
    [InlineData("e")]
    [InlineData("en-")]
    public void Validate_rejects_malformed_values(string? language)
    {
        var error = Assert.Throws<ArgumentException>(() => ConversationLanguagePolicy.Validate(language));
        Assert.Contains("language", error.Message, StringComparison.OrdinalIgnoreCase);
    }

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
