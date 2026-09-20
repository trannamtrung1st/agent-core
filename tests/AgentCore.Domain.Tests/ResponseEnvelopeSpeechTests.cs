using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class ResponseEnvelopeSpeechTests
{
    [Fact]
    public void Same_is_valid_with_null_text_and_required_display()
    {
        var envelope = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Same, null),
            [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, true)]);
        Assert.Equal(ResponseSpeechMode.Same, envelope.SpeechMode);
        Assert.Null(envelope.SpeechText);
        Assert.Null(envelope.Speech.Text);
        Assert.Null(envelope.PublicCustomSpeech());
        Assert.Equal("**Hi**", Assert.Single(envelope.Blocks).DisplayText);
    }

    [Fact]
    public void Custom_requires_non_empty_text()
    {
        var envelope = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Spoken"),
            []);
        Assert.Equal(ResponseSpeechMode.Custom, envelope.Speech.Mode);
        Assert.Equal("Spoken", envelope.Speech.Text);
        Assert.Equal("Spoken", envelope.PublicCustomSpeech());
        Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create("Hello", new ResponseSpeech(ResponseSpeechMode.Custom, null), []));
        Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create("Hello", new ResponseSpeech(ResponseSpeechMode.Custom, "  "), []));
    }

    [Fact]
    public void None_forbids_text()
    {
        var envelope = ResponseEnvelope.Create(
            "Diagram only",
            new ResponseSpeech(ResponseSpeechMode.None, null),
            [new ResponseBlock("b1", ResponseBlockKind.Markdown, "| a |", "| a |", null, null, false)]);
        Assert.Equal(ResponseSpeechMode.None, envelope.SpeechMode);
        Assert.Null(envelope.SpeechText);
        Assert.Null(envelope.PublicCustomSpeech());
        Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create("Diagram only", new ResponseSpeech(ResponseSpeechMode.None, "nope"), []));
    }

    [Fact]
    public void Whitespace_only_display_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create("  \n", new ResponseSpeech(ResponseSpeechMode.Same, null), []));
        Assert.Contains("displayText", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create(
                "   ",
                new ResponseSpeech(ResponseSpeechMode.None, null),
                [new ResponseBlock("b1", ResponseBlockKind.Markdown, "table", "table", null, null, false)]));
    }

    [Fact]
    public void Same_rejects_non_null_text()
    {
        Assert.Throws<ArgumentException>(() =>
            ResponseEnvelope.Create("Hello", new ResponseSpeech(ResponseSpeechMode.Same, "Hello"), []));
    }

    [Fact]
    public void Public_custom_speech_is_omitted_when_not_meaningfully_distinct()
    {
        var sameWords = ResponseEnvelope.Create(
            "Hello there",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Hello there"),
            []);
        Assert.Null(sameWords.PublicCustomSpeech());
        var distinct = ResponseEnvelope.Create(
            "Hello there",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Hi"),
            []);
        Assert.Equal("Hi", distinct.PublicCustomSpeech());
    }

    [Fact]
    public void Merge_delivery_still_preserves_block_flags_and_speech_mode()
    {
        var durable = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Spoken"),
            [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, true)]);
        var current = durable with
        {
            Blocks = [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, false)]
        };
        var merged = ResponseEnvelopeParser.MergeDelivery(durable, current);
        Assert.True(Assert.Single(merged.Blocks).DisplayDelivered);
        Assert.Equal(ResponseSpeechMode.Custom, merged.SpeechMode);
        Assert.Equal("Spoken", merged.SpeechText);
    }
}
