using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Tests;

public sealed class MarkerSemanticResponseParserTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void First_speech_marker_wins_custom_before_none()
    {
        var parsed = MarkerSemanticResponseParser.Parse(
            "Answer.[[speech:Speak]][[speech:none]]",
            finalize: true);
        Assert.Equal("Answer.", parsed.DisplayText);
        Assert.Equal(ModelSpeechMode.Custom, parsed.Speech.Mode);
        Assert.Equal("Speak", parsed.Speech.Text);
    }

    [Fact]
    public void First_speech_marker_wins_none_before_custom()
    {
        var parsed = MarkerSemanticResponseParser.Parse(
            "Answer.[[speech:none]][[speech:Speak]]",
            finalize: true);
        Assert.Equal("Answer.", parsed.DisplayText);
        Assert.Equal(ModelSpeechMode.None, parsed.Speech.Mode);
        Assert.Null(parsed.Speech.Text);
    }

    [Fact]
    public void Chunked_feed_keeps_first_speech_marker_across_chunks()
    {
        var parser = new MarkerSemanticResponseParser();
        _ = parser.Feed("Visible.[[speech:Speak this]]");
        Assert.True(parser.TryPeekSpeechReady(out var peek));
        Assert.Equal(ModelSpeechMode.Custom, peek!.Speech.Mode);
        Assert.True(parser.TryFinish(out var finished, out _));
        Assert.Equal(ModelSpeechMode.Custom, finished!.Speech.Mode);
        Assert.Equal("Speak this", finished.Speech.Text);

        var noneFirst = new MarkerSemanticResponseParser();
        _ = noneFirst.Feed("Visible.[[speech:none]]");
        Assert.False(noneFirst.TryPeekSpeechReady(out _));
        noneFirst.Feed("[[speech:Later custom]]");
        Assert.True(noneFirst.TryFinish(out var noneFinal, out _));
        Assert.Equal(ModelSpeechMode.None, noneFinal!.Speech.Mode);
    }

    [Fact]
    public void Speech_none_marker_maps_to_none_mode()
    {
        var parsed = MarkerSemanticResponseParser.Parse(
            "Chart only.[[speech:none]]",
            finalize: true);
        Assert.Equal("Chart only.", parsed.DisplayText);
        Assert.Equal(ModelSpeechMode.None, parsed.Speech.Mode);
        Assert.Null(parsed.Speech.Text);
    }

    [Fact]
    public void Strips_markers_and_keeps_display_independent_of_speech()
    {
        var parsed = MarkerSemanticResponseParser.Parse(
            "Hello[[speech:Spoken hello]][[md:**bold**]][[artifact:fixture-artifact-1]][[xyz:nope]] world",
            finalize: true);
        Assert.Equal("Hello world", parsed.DisplayText);
        Assert.Equal(ModelSpeechMode.Custom, parsed.Speech.Mode);
        Assert.Equal("Spoken hello", parsed.Speech.Text);
        Assert.Equal(3, parsed.Blocks.Count);
        Assert.Equal(ModelResponseBlockKind.Markdown, parsed.Blocks[0].Kind);
        Assert.Equal("**bold**", parsed.Blocks[0].Text);
        Assert.Equal(ModelResponseBlockKind.ArtifactReference, parsed.Blocks[1].Kind);
        Assert.Equal("fixture-artifact-1", parsed.Blocks[1].ArtifactId);
        Assert.Equal(ModelResponseBlockKind.Unknown, parsed.Blocks[2].Kind);
    }

    [Fact]
    public void Incomplete_markers_never_leak_syntax_into_display()
    {
        var streaming = MarkerSemanticResponseParser.Parse("Hello [[md:**bo", finalize: false);
        Assert.Equal("Hello ", streaming.DisplayText);
        Assert.Empty(streaming.Blocks);
        var done = MarkerSemanticResponseParser.Parse("Hello [[md:**bo", finalize: true);
        Assert.Equal("Hello ", done.DisplayText);
        Assert.DoesNotContain("[[", done.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_prose_raw_model_text_matches_display_text_after_parse()
    {
        foreach (var caseDef in LoadFixture().OrdinaryProse)
        {
            var parsed = MarkerSemanticResponseParser.Parse(caseDef.RawFinalModelText, finalize: true);
            Assert.Equal(caseDef.ExpectedDisplayText, parsed.DisplayText);
            if (caseDef.ExpectedSpeechText is not null)
            {
                Assert.Equal(ModelSpeechMode.Custom, parsed.Speech.Mode);
                Assert.Equal(caseDef.ExpectedSpeechText, parsed.Speech.Text);
            }
        }
    }

    [Fact]
    public void Model_formatting_quality_cases_preserve_display_text_unchanged_by_parser()
    {
        foreach (var caseDef in LoadFixture().ModelFormattingQuality)
        {
            var parsed = MarkerSemanticResponseParser.Parse(caseDef.RawFinalModelText, finalize: true);
            Assert.Equal(caseDef.ExpectedDisplayText, parsed.DisplayText);
            Assert.Equal(caseDef.RawFinalModelText, parsed.DisplayText);
        }
    }

    private static DisplayPipelineFixture LoadFixture()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "display-pipeline.json"));
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DisplayPipelineFixture>(json, Json)
            ?? throw new InvalidOperationException("Fixture deserialize failed.");
    }

    private sealed class DisplayPipelineFixture
    {
        public List<OrdinaryProseCase> OrdinaryProse { get; set; } = [];
        public List<ModelFormattingCase> ModelFormattingQuality { get; set; } = [];
    }

    private sealed class OrdinaryProseCase
    {
        public string Id { get; set; } = "";
        public string RawFinalModelText { get; set; } = "";
        public string ExpectedDisplayText { get; set; } = "";
        public string? ExpectedSpeechText { get; set; }
    }

    private sealed class ModelFormattingCase
    {
        public string Id { get; set; } = "";
        public string RawFinalModelText { get; set; } = "";
        public string ExpectedDisplayText { get; set; } = "";
        public bool ExpectCodeBlock { get; set; }
    }
}
