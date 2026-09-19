using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class DisplayPipelineRegressionTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Ordinary_prose_raw_model_text_matches_display_text_after_parse()
    {
        foreach (var caseDef in LoadFixture().OrdinaryProse)
        {
            var parsed = ResponseEnvelopeParser.Parse(caseDef.RawFinalModelText, finalize: true);
            Assert.Equal(caseDef.ExpectedDisplayText, parsed.DisplayText);
            if (caseDef.ExpectedSpeechText is not null)
            {
                Assert.Equal(caseDef.ExpectedSpeechText, parsed.SpeechText);
            }

            var merged = ResponseEnvelopeParser.MergeDelivery(parsed, parsed);
            Assert.Equal(caseDef.ExpectedDisplayText, merged.DisplayText);
        }
    }

    [Fact]
    public void Model_formatting_quality_cases_preserve_display_text_unchanged_by_parser()
    {
        foreach (var caseDef in LoadFixture().ModelFormattingQuality)
        {
            var parsed = ResponseEnvelopeParser.Parse(caseDef.RawFinalModelText, finalize: true);
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
