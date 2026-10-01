using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Tests;

public sealed class NativeSemanticResponseParserTests
{
    [Theory]
    [InlineData("", ProviderFailureReason.ResponseFunctionArgumentsInvalid)]
    [InlineData("not-json", ProviderFailureReason.InvalidJson)]
    [InlineData("""{"speech":{"mode":"same"}}""", ProviderFailureReason.MissingDisplayText)]
    [InlineData("""{"displayText":"","speech":{"mode":"same"}}""", ProviderFailureReason.MissingDisplayText)]
    [InlineData(
        """{"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}""",
        ProviderFailureReason.MissingDisplayText)]
    [InlineData(
        """{"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"   ","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}""",
        ProviderFailureReason.MissingDisplayText)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"maybe"}}""", ProviderFailureReason.InvalidSpeechMode)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"custom","text":null}}""", ProviderFailureReason.MissingCustomSpeechText)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"sessionId":"other"}""", ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"destination":"other"}""", ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"profileId":"p"}""", ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"tenant":"t"}""", ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"recipient":"u"}""", ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"action":"teams.reply"}""", ProviderFailureReason.UnknownAction)]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"disposition":"Frobnicate"}""", ProviderFailureReason.UnknownDisposition)]
    [InlineData("not-json-step", ProviderFailureReason.InvalidJson)]
    [InlineData(
        """{"displayText":"Shown","speech":{"mode":"same"},"memory":[{"operation":"upsert","kind":"fact","subject":"x"}]}""",
        ProviderFailureReason.InvalidMemoryProposal)]
    public void TryParse_returns_a_bounded_failure_reason(string json, string expectedReason)
    {
        Assert.False(NativeSemanticResponseParser.TryParse(json, out _, out var reason));
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void TryParse_accepts_a_valid_envelope()
    {
        var json = """
            {"displayText":"Shown","speech":{"mode":"same"},"blocks":[],"memory":[{"operation":"upsert","kind":"fact","subject":"editor","content":"Rider","source":"userExplicit"}]}
            """;
        Assert.True(NativeSemanticResponseParser.TryParse(json, out var response, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.Equal("Shown", response!.DisplayText);
        Assert.Equal("Rider", Assert.Single(response.Memory!).Content);
    }

    [Theory]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same","text":null}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same","text":""}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same","text":"redundant provider text"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"maybe","text":"ignored"}}""")]
    public void Text_contract_canonicalizes_harmless_same_speech(string json)
    {
        var contract = new ModelResponseContract(SpeechWillBeUsed: false);
        Assert.True(NativeSemanticResponseParser.TryParse(json, out var response, out var reason, contract));
        Assert.Equal(string.Empty, reason);
        Assert.Equal("Shown", response!.DisplayText);
        Assert.Equal(ModelSpeechMode.Same, response.Speech.Mode);
        Assert.Null(response.Speech.Text);
    }

    [Fact]
    public void Voice_contract_ignores_same_text_and_rejects_unknown_mode()
    {
        var voice = new ModelResponseContract(SpeechWillBeUsed: true);
        Assert.True(NativeSemanticResponseParser.TryParse(
            """{"displayText":"Shown","speech":{"mode":"same","text":"spoken"}}""",
            out var same,
            out var sameReason,
            voice));
        Assert.Equal(string.Empty, sameReason);
        Assert.Equal(ModelSpeechMode.Same, same!.Speech.Mode);
        Assert.Null(same.Speech.Text);
        Assert.False(NativeSemanticResponseParser.TryParse(
            """{"displayText":"Shown","speech":{"mode":"maybe"}}""",
            out _,
            out var modeReason,
            voice));
        Assert.Equal(ProviderFailureReason.InvalidSpeechMode, modeReason);
    }
}
