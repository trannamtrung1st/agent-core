using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class ResponseEnvelopeParserTests
{
    [Fact]
    public void Merge_delivery_keeps_current_envelope_when_no_prior_blocks()
    {
        var current = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Same, null),
            [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, false)]);
        var merged = ResponseEnvelopeParser.MergeDelivery(null, current);
        Assert.Same(current, merged);
        Assert.False(Assert.Single(merged.Blocks).DisplayDelivered);
    }

    [Fact]
    public void Fallback_constants_remain_for_receipt_mapping()
    {
        Assert.Equal("[Unsupported content]", ResponseEnvelopeParser.UnsupportedFallback);
        Assert.Equal("[Unavailable artifact]", ResponseEnvelopeParser.UnauthorizedArtifactFallback);
    }
}
