using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class ResponseEnvelopeParserTests
{
    [Fact]
    public void Strips_markers_and_keeps_display_independent_of_speech()
    {
        var parsed = ResponseEnvelopeParser.Parse(
            "Hello[[speech:Spoken hello]][[md:**bold**]][[artifact:fixture-artifact-1]][[xyz:nope]] world",
            id => id == "fixture-artifact-1",
            finalize: true);
        Assert.Equal("Hello world", parsed.DisplayText);
        Assert.Equal("Spoken hello", parsed.SpeechText);
        Assert.Equal(3, parsed.Blocks.Count);
        Assert.Equal(ResponseBlockKind.Markdown, parsed.Blocks[0].Kind);
        Assert.Equal("**bold**", parsed.Blocks[0].DisplayText);
        Assert.Equal(ResponseBlockKind.ArtifactReference, parsed.Blocks[1].Kind);
        Assert.Equal("fixture-artifact-1", parsed.Blocks[1].ArtifactId);
        Assert.Equal(ResponseBlockKind.Unknown, parsed.Blocks[2].Kind);
        Assert.Equal(ResponseEnvelopeParser.UnsupportedFallback, parsed.Blocks[2].FallbackText);
    }

    [Fact]
    public void Unauthorized_artifact_becomes_unknown_without_exposing_id()
    {
        var parsed = ResponseEnvelopeParser.Parse(
            "See [[artifact:secret-id]]",
            _ => false,
            finalize: true);
        Assert.Equal("See ", parsed.DisplayText);
        Assert.Equal(ResponseBlockKind.Unknown, parsed.Blocks[0].Kind);
        Assert.Null(parsed.Blocks[0].ArtifactId);
        Assert.Equal(ResponseEnvelopeParser.UnauthorizedArtifactFallback, parsed.Blocks[0].FallbackText);
    }

    [Fact]
    public void Incomplete_marker_is_held_until_finalize()
    {
        var streaming = ResponseEnvelopeParser.Parse("Hello [[md:**bo", finalize: false);
        Assert.Equal("Hello ", streaming.DisplayText);
        Assert.Empty(streaming.Blocks);
        var done = ResponseEnvelopeParser.Parse("Hello [[md:**bo", finalize: true);
        Assert.Equal("Hello [[md:**bo", done.DisplayText);
    }
}
