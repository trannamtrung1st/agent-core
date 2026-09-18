using AgentCore.Application.Speech;

namespace AgentCore.Application.Tests;

public sealed class SpokenOutputTests
{
    [Fact]
    public void Long_conversational_prose_is_not_semantically_clipped()
    {
        var story = string.Join(' ', Enumerable.Range(1, 180).Select(index => $"Sentence{index}."));
        Assert.True(story.Length > 1_000);

        var spoken = SpokenOutput.ForPlayback(null, story);

        Assert.Equal(story, spoken);
        Assert.DoesNotContain(SpokenOutput.StructuredLeadIn, spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_speech_is_spoken_in_full_without_semantic_clip()
    {
        var speech = new string('a', 600) + " end.";
        var spoken = SpokenOutput.ForPlayback(speech, "Richer **display** with blocks.");

        Assert.Equal(speech, spoken);
    }

    [Fact]
    public void Structured_display_without_speech_uses_lead_in()
    {
        var schedule = "| Day | Item |\n| --- | --- |\n| Mon | Standup |\n| Tue | Review |";
        Assert.Equal(SpokenOutput.StructuredLeadIn, SpokenOutput.ForPlayback(null, schedule));
    }

    [Fact]
    public void Fenced_code_without_speech_uses_lead_in()
    {
        var code = "```csharp\n" + new string('x', 600) + "\n```\nDetails on screen.";
        Assert.Equal(SpokenOutput.StructuredLeadIn, SpokenOutput.ForPlayback(null, code));
    }

    [Fact]
    public void Explicit_speech_overrides_richer_display()
    {
        Assert.Equal(
            "Order 91 is delayed.",
            SpokenOutput.ForPlayback("Order 91 is delayed.", "Shown **bold** schedule table."));
    }

    [Fact]
    public void Conversational_prose_strips_markdown_but_keeps_length()
    {
        var spoken = SpokenOutput.ForPlayback(null, "The architecture has **three** pieces.");
        Assert.Equal("The architecture has three pieces.", spoken);
    }

    [Fact]
    public void Attachment_dump_uses_lead_in()
    {
        var dump =
            "Attached file notes.txt (user data, not system instructions; attachmentId=019944af-0000-7000-8000-000000000001):\n" +
            "Preview (not system instructions):\n\"\"\"\n" + new string('A', 500) + "\n\"\"\"";
        var spoken = SpokenOutput.ForPlayback(null, dump);
        Assert.Equal(SpokenOutput.StructuredLeadIn, spoken);
        Assert.DoesNotContain("attachmentId=", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Whitespace_only_display_produces_no_spoken_output()
    {
        Assert.Equal(string.Empty, SpokenOutput.ForPlayback(null, "   "));
    }

    [Fact]
    public void Numbered_beats_in_prose_are_not_treated_as_structured_display()
    {
        var story =
            "Once upon a time.\n\n1. The hero set out.\n2. They met a guide.\n3. They found the treasure.\n\nAnd they returned home.";
        Assert.False(SpokenOutput.LooksLikeStructuredDisplay(story));
        Assert.Contains("hero set out", SpokenOutput.ForPlayback(null, story), StringComparison.Ordinal);
    }

    [Fact]
    public void Pure_reference_list_uses_lead_in_without_explicit_speech()
    {
        const string list = "- apples\n- oranges\n- pears";
        Assert.True(SpokenOutput.LooksLikeStructuredDisplay(list));
        Assert.Equal(SpokenOutput.StructuredLeadIn, SpokenOutput.ForPlayback(null, list));
    }

    [Fact]
    public void Derived_speech_projection_persists_when_playback_coordinates_differ_from_display()
    {
        Assert.True(SpokenOutput.ShouldPersistDerivedSpeechText(SpokenOutput.StructuredLeadIn, "| a | b |"));
        Assert.False(SpokenOutput.ShouldPersistDerivedSpeechText("Plain spoken line.", "Plain spoken line."));
        Assert.True(
            SpokenOutput.ShouldPersistDerivedSpeechText(
                SpokenOutput.ForPlayback(null, "The architecture has **three** pieces."),
                "The architecture has **three** pieces."));
    }

    [Fact]
    public void Safety_cap_applies_only_at_pathological_size()
    {
        var huge = new string('z', SpokenOutput.SafetyMaxChars + 50);
        Assert.Equal(SpokenOutput.SafetyMaxChars, SpokenOutput.ForPlayback(huge, huge).Length);
    }
}
