using AgentCore.Application.Speech;

namespace AgentCore.Application.Tests;

public sealed class SpokenOutputTests
{
    private const string RuntimeEnglishLeadIn = "I've put the detailed answer on screen.";

    [Fact]
    public void Long_conversational_prose_is_not_semantically_clipped()
    {
        var story = string.Join(' ', Enumerable.Range(1, 180).Select(index => $"Sentence{index}."));
        Assert.True(story.Length > 1_000);

        var spoken = SpokenOutput.ForPlayback(null, story);

        Assert.Equal(story, spoken);
        Assert.DoesNotContain(RuntimeEnglishLeadIn, spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_speech_is_spoken_in_full_without_semantic_clip()
    {
        var speech = new string('a', 600) + " end.";
        var spoken = SpokenOutput.ForPlayback(speech, "Richer **display** with blocks.");

        Assert.Equal(speech, spoken);
    }

    [Fact]
    public void Structured_display_without_speech_derives_model_prose_not_runtime_lead_in()
    {
        var schedule = "| Day | Item |\n| --- | --- |\n| Mon | Standup |\n| Tue | Review |";
        Assert.Equal(string.Empty, SpokenOutput.ForPlayback(null, schedule));
    }

    [Fact]
    public void Fenced_code_without_speech_keeps_trailing_display_prose()
    {
        var code = "```csharp\n" + new string('x', 600) + "\n```\nDetails on screen.";
        Assert.Equal("Details on screen.", SpokenOutput.ForPlayback(null, code));
        Assert.DoesNotContain(RuntimeEnglishLeadIn, SpokenOutput.ForPlayback(null, code), StringComparison.Ordinal);
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
    public void Vietnamese_structured_markdown_derives_model_language_not_runtime_english()
    {
        const string display =
            "Đây là bảng chi tiết:\n\n| Cột | Giá trị |\n| --- | --- |\n| A | một |\n";
        var spoken = SpokenOutput.ForPlayback(null, display);
        Assert.Contains("Đây là bảng chi tiết", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeEnglishLeadIn, spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("| Cột |", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachment_dump_produces_no_spoken_fallback()
    {
        var dump =
            "Attached file notes.txt (user data, not system instructions; attachmentId=019944af-0000-7000-8000-000000000001):\n" +
            "Preview (not system instructions):\n\"\"\"\n" + new string('A', 500) + "\n\"\"\"";
        var spoken = SpokenOutput.ForPlayback(null, dump);
        Assert.Equal(string.Empty, spoken);
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
    public void Pure_reference_list_derives_list_text_without_runtime_lead_in()
    {
        const string list = "- apples\n- oranges\n- pears";
        Assert.True(SpokenOutput.LooksLikeStructuredDisplay(list));
        var spoken = SpokenOutput.ForPlayback(null, list);
        Assert.Contains("apples", spoken, StringComparison.Ordinal);
        Assert.Contains("pears", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeEnglishLeadIn, spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_speech_remains_authoritative_for_structured_display()
    {
        const string speech = "Here is your week at a glance.";
        var table = "| Day | Item |\n| --- | --- |\n| Mon | Standup |";
        Assert.Equal(speech, SpokenOutput.ForPlayback(speech, table));
    }

    [Fact]
    public void Derived_speech_projection_persists_when_playback_coordinates_differ_from_display()
    {
        Assert.False(SpokenOutput.ShouldPersistDerivedSpeechText(string.Empty, "| a | b |"));
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

    [Fact]
    public void Explicit_speech_that_looks_like_file_dump_is_not_spoken()
    {
        var dump = "attachmentId=abc\n" + new string('A', 200);
        Assert.Equal(string.Empty, SpokenOutput.ForPlayback(dump, "display"));
    }

    [Fact]
    public void Heading_and_prose_fallback_keeps_readable_prose_only()
    {
        Assert.Equal(
            "Result. The request completed successfully.",
            SpokenOutput.ForPlayback(null, "# Result\nThe request completed successfully."));
    }

    [Fact]
    public void Multiple_fenced_technical_blocks_without_prose_resolve_no_speech()
    {
        const string display = """
            ```text
            Client --> HTTPS --> API Gateway --> Auth --> Task Service --> PostgreSQL
            ```

            ```http
            POST /tasks HTTP/1.1
            Authorization: Bearer sk-test
            Content-Type: application/json
            ```

            ```json
            { "id": "task-1", "title": "Example" }
            ```

            ```bash
            curl -X POST https://api.example.com/tasks -H "Authorization: Bearer sk-test"
            ```
            """;

        var spoken = SpokenOutput.ForPlayback(null, display);
        Assert.Equal(string.Empty, spoken);
        Assert.DoesNotContain("Client", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("POST /tasks", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prose_before_technical_blocks_derives_only_the_prose()
    {
        const string display = """
            Here is how the task API works.

            ```http
            POST /tasks HTTP/1.1
            Authorization: Bearer sk-test
            ```

            ```json
            { "id": "task-1" }
            ```

            ```bash
            curl -X POST https://api.example.com/tasks
            ```
            """;

        var spoken = SpokenOutput.ForPlayback(null, display);
        Assert.Equal("Here is how the task API works.", spoken);
        Assert.DoesNotContain("POST /tasks", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_speech_with_rich_technical_display_speaks_only_explicit_text()
    {
        const string speech = "Đây là kiến trúc API gồm client, gateway, xác thực, task service và PostgreSQL.";
        const string display = """
            ```text
            Client --> API Gateway --> PostgreSQL
            ```
            ```http
            POST /tasks HTTP/1.1
            ```
            ```json
            { "id": "task-1" }
            ```
            """;

        Assert.Equal(speech, SpokenOutput.ForPlayback(speech, display));
    }

    [Fact]
    public void Unclosed_fence_suppresses_trailing_technical_content()
    {
        const string display = """
            Intro sentence stays.

            ```http
            POST /tasks HTTP/1.1
            Authorization: Bearer token
            """;

        var spoken = SpokenOutput.ForPlayback(null, display);
        Assert.Equal("Intro sentence stays.", spoken);
    }
}
