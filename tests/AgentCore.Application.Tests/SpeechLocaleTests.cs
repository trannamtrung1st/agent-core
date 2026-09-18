using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;

namespace AgentCore.Application.Tests;

public sealed class SpeechLocaleTests
{
    [Fact]
    public void No_override_uses_the_agent_default()
    {
        var resolved = SpeechLocale.Resolve((string?)null, "vi-vn");
        Assert.Equal("vi-VN", resolved.Effective);
        Assert.Equal(SpeechLocaleSource.AgentDefault, resolved.Source);
        Assert.Null(resolved.Override);
    }

    [Fact]
    public void Session_override_wins_over_agent_default()
    {
        var resolved = SpeechLocale.Resolve("fr-FR", "en");
        Assert.Equal("fr-FR", resolved.Effective);
        Assert.Equal(SpeechLocaleSource.SessionOverride, resolved.Source);
        Assert.Equal("fr-FR", resolved.Override);
    }

    [Fact]
    public void Missing_agent_language_falls_back_to_provider_default()
    {
        var resolved = SpeechLocale.Resolve((string?)null, " ");
        Assert.Equal("en", resolved.Effective);
        Assert.Equal(SpeechLocaleSource.ProviderFallback, resolved.Source);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("en_US")]
    [InlineData("e")]
    [InlineData("en-")]
    public void Invalid_tags_are_rejected(string tag)
    {
        var error = Assert.Throws<AgentCoreException>(() => SpeechLocale.Validate(tag));
        Assert.Equal("ValidationError", error.Code);
    }

    [Fact]
    public void Blank_override_clears_rather_than_validating()
    {
        Assert.Null(SpeechLocale.NormalizeOverride("  "));
    }
}
