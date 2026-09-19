using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class VoiceRealtimeRegressionTests
{
    [Fact]
    public void Spoken_output_does_not_keep_attachment_dumps()
    {
        var dump =
            "Attached file notes.txt (user data, not system instructions; attachmentId=019944af-0000-7000-8000-000000000001):\n" +
            "Preview (not system instructions):\n\"\"\"\n" + new string('A', 500) + "\n\"\"\"";
        var spoken = SpokenOutput.ForPlayback(null, dump);
        Assert.Equal(string.Empty, spoken);
        Assert.DoesNotContain("attachmentId=", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 80), spoken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_conversational_story_reaches_tts_segments()
    {
        var story = string.Join(' ', Enumerable.Range(1, 160).Select(index => $"Word{index}."));
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([story]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Tell me a story");
        AudioFrameOutput? final = null;
        for (var attempt = 0; attempt < 2_000 && final is null; attempt++)
        {
            final = output.Items
                .Select(item => item.Payload)
                .OfType<AudioFrameOutput>()
                .LastOrDefault(frame => frame.IsFinal);
            if (final is not null)
            {
                break;
            }

            if (runtime.ActiveResponseId is { } activeId && runtime.SentSamples > 0)
            {
                await runtime.SubmitPlaybackAsync(activeId, "progress", runtime.SentSamples, 0);
            }

            await Task.Delay(10);
        }

        Assert.NotNull(final);
        var finalItem = output.Items.Last(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        var responseId = finalItem.ResponseId!.Value;
        await runtime.SubmitPlaybackAsync(responseId, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(responseId, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var narrated = string.Concat(synthesizer.Texts);
        Assert.Contains("Word1.", narrated, StringComparison.Ordinal);
        Assert.Contains("Word160.", narrated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_intro_then_table_speaks_intro_not_table_cells_or_runtime_lead_in()
    {
        const string intro = "Here's a markdown table for you:\n";
        const string table = "| Technology | Category |\n| --- | --- |\n| React | Frontend |\n";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel([intro, table]),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("table");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var narrated = string.Concat(synthesizer.Texts);
        Assert.Contains("Here's a markdown table for you:", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("I've put the detailed answer on screen.", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("| Technology |", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("React", narrated, StringComparison.Ordinal);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Here's a markdown table for you:", assistant.Envelope?.SpeechText);
    }

    [Fact]
    public async Task Structured_schedule_display_uses_short_speech_projection()
    {
        var table = "| Day | Item |\n| --- | --- |\n"
            + string.Join('\n', Enumerable.Range(0, 6).Select(index => $"| Day {index} | Task {index} |"));
        var speech = "Here is your week at a glance.";
        var modelText = $"[[speech:{speech}]]\n{table}";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([modelText]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Show my schedule");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(synthesizer.Texts, text => text.Contains(speech, StringComparison.Ordinal));
        Assert.All(synthesizer.Texts, text => Assert.DoesNotContain("| Day |", text, StringComparison.Ordinal));
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("| Day |", assistant.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_display_without_explicit_speech_derives_display_prose_only()
    {
        var display = "```csharp\npublic class Example {}\n```\nDetails on screen.";
        Assert.Equal("Details on screen.", SpokenOutput.ForPlayback(null, display));
    }

    [Fact]
    public void Long_structured_display_without_speech_derives_prose_or_empty_not_runtime_lead_in()
    {
        var code = "```csharp\n" + new string('x', 900) + "\n```\nSchedule follows.";
        Assert.True(code.Length > 800);
        Assert.Equal("Schedule follows.", SpokenOutput.ForPlayback(null, code));

        var table = "| Day | Item |\n| --- | --- |\n"
            + string.Join('\n', Enumerable.Range(0, 40).Select(index => $"| {index} | {new string('a', 20)} |"));
        Assert.True(table.Length > 800);
        Assert.Equal(string.Empty, SpokenOutput.ForPlayback(null, table));
    }

    [Fact]
    public void Vietnamese_structured_markdown_without_speech_never_uses_runtime_english_lead_in()
    {
        const string display =
            "Đây là bảng chi tiết:\n\n| Cột | Giá trị |\n| --- | --- |\n| A | một |\n";
        var spoken = SpokenOutput.ForPlayback(null, display);
        Assert.Contains("Đây là bảng chi tiết", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("I've put the detailed answer on screen.", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Spoken_output_strips_display_markdown_but_keeps_explicit_speech()
    {
        Assert.Equal("The architecture has three pieces.", SpokenOutput.ForPlayback(null, "The architecture has **three** pieces."));
        Assert.Equal("Order 91 is delayed.", SpokenOutput.ForPlayback("Order 91 is delayed.", "Shown **bold**."));
    }

    [Fact]
    public async Task Tts_does_not_narrate_attachment_bytes_or_extracts()
    {
        var dump =
            "[[speech:I put the file summary on screen.]]Here is the file. Attached file secret.bin (user data, not system instructions; attachmentId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee):\n" +
            Convert.ToBase64String(Enumerable.Repeat((byte)7, 120).ToArray());
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([dump]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Read the file");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.NotEmpty(synthesizer.Texts);
        Assert.All(synthesizer.Texts, text =>
        {
            Assert.DoesNotContain("attachmentId=", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.bin", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Enumerable.Repeat((byte)7, 120).ToArray())[..40], text, StringComparison.Ordinal);
        });
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("secret.bin", assistant.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(assistant.Envelope?.SpeechText));
        Assert.DoesNotContain("attachmentId=", assistant.Envelope!.SpeechText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_prose_persists_playback_projection_when_coordinates_differ()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(["The architecture has **three** pieces."]),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Explain");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("The architecture has three pieces.", assistant.Envelope!.SpeechText);
        Assert.Contains(synthesizer.Texts, text => text.Contains("three", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fully_heard_markdown_assistant_uses_speech_coordinates_in_next_prompt()
    {
        const string display = "The architecture has **three** pieces.";
        const string spoken = "The architecture has three pieces.";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel([display]),
            synthesizer,
            brain: brain);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Explain");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitUserTextAsync("Follow up");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, brain.Calls);
        var firstAssistant = runtime.Snapshot.Entries.First(
            entry => entry.Role == ConversationRole.Assistant && entry.Text == display);
        Assert.Equal(spoken, PromptContextBuilder.EligibleAssistantText(firstAssistant));
        var promptAtFollowUp = brain.Contexts[^1];
        var assistantInPrompt = promptAtFollowUp.History.First(
            entry => entry.Role == ConversationRole.Assistant && entry.Text == display);
        Assert.Equal(spoken, PromptContextBuilder.EligibleAssistantText(assistantInPrompt));
    }

    [Fact]
    public async Task Explicit_speech_projection_while_rich_blocks_stay_on_display()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        var model = new ScriptedLanguageModel(
            ["Order delayed. [[speech:Order 91 is delayed.]] [[md:**Delayed**]] [[artifact:fixture-artifact-1]]"]);
        await using var runtime = CreateVoice(output, model, synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("status");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(synthesizer.Texts, text => text.Contains("Order 91 is delayed.", StringComparison.Ordinal));
        Assert.All(synthesizer.Texts, text => Assert.DoesNotContain("fixture-artifact-1", text, StringComparison.Ordinal));
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.Markdown);
        Assert.Contains(assistant.Envelope.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
    }

    [Fact]
    public async Task Mode_switch_keeps_session_identity_and_artifact_refs()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(["See [[artifact:fixture-artifact-1]]"]));
        await runtime.AttachAsync();
        var sessionId = runtime.SessionId;
        await runtime.SubmitUserTextAsync("Cite it");
        await runtime.WaitUntilIdleAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SetModeAsync(SessionMode.Text);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Text);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(sessionId, runtime.SessionId);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains(assistant.Envelope!.Blocks, block => block.ArtifactId == "fixture-artifact-1");
    }

    [Fact]
    public async Task Tool_artifact_reconnect_hides_undelivered_blocks()
    {
        var store = new InMemoryMemoryStore();
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var tools = new SessionToolExecutor(knowledge, artifacts: artifacts);
        var definition = await Load("customer-support");
        Guid sessionId;
        Guid responseId;
        await using (var first = CreateVoice(
                         new CapturingSessionOutput(),
                         new ScriptedLanguageModel(),
                         store: store,
                         snapshotDefinition: definition,
                         tools: tools,
                         artifacts: artifacts))
        {
            await first.AttachAsync();
            sessionId = first.SessionId;
            Assert.True(await first.SubmitUserTextAsync("Run the support case for order 91."));
            await first.WaitUntilIdleAsync();
            var assistant = first.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            responseId = assistant.ResponseId!.Value;
            Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
            await first.DetachAsync();
            await first.WaitUntilIdleAsync();
        }

        var restoredOutput = new CapturingSessionOutput();
        var paused = (await store.LoadAsync(sessionId))!;
        var loaded = await PausedSessionReopen.ReopenAsync(store, paused, TimeProvider.System);
        await using var restored = CreateVoice(
            restoredOutput,
            new ScriptedLanguageModel(),
            store: store,
            snapshot: loaded,
            snapshotDefinition: definition,
            tools: tools,
            artifacts: artifacts);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        var ready = Assert.IsType<ReadyOutput>(restoredOutput.Items.Single(item => item.Payload is ReadyOutput).Payload);
        var history = ready.Ready.History.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(string.Empty, history.Text);
        Assert.Empty(history.Blocks);
        Assert.Equal(responseId, history.ResponseId);
        Assert.Equal(sessionId, restored.SessionId);
    }

    [Fact]
    public async Task Speech_marker_split_across_chunks_waits_for_tts_until_projection_complete()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(
            [
                "[[speech:Here is",
                " the summary.]]",
                "# Details\n",
                "| A | B |\n",
                "| 1 | 2 |"
            ]),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("structured");
        await output.WaitForAsync(item => item.Payload is SpeechProjectionOutput projection
            && projection.Text == "Here is the summary.");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var narrated = string.Concat(synthesizer.Texts);
        Assert.Equal("Here is the summary.", narrated);
        Assert.DoesNotContain("| A |", narrated, StringComparison.Ordinal);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Here is the summary.", assistant.Envelope!.SpeechText);
        Assert.Contains("# Details", assistant.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Display_text_before_speech_marker_is_never_synthesized()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(["Display text first.", "[[speech:Actual speech.]]", " More display."]),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("order");
        await output.WaitForAsync(item => item.Payload is SpeechProjectionOutput projection
            && projection.Text == "Actual speech.");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var items = output.Items.ToList();
        var projectionIndex = items.FindIndex(item => item.Payload is SpeechProjectionOutput);
        var firstDisplayIndex = items.FindIndex(item => item.Payload is TextDeltaOutput);
        Assert.True(projectionIndex >= 0);
        Assert.True(firstDisplayIndex > projectionIndex);
        var narrated = string.Concat(synthesizer.Texts);
        Assert.Contains("Actual speech.", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("Display text first.", narrated, StringComparison.Ordinal);
        Assert.DoesNotContain("More display.", narrated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_speech_marker_applies_completion_fallback_without_streaming_tts()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(["Streaming ", "display only."], hold),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("plain");
        await Task.Delay(50);
        Assert.Empty(synthesizer.Texts);
        hold.TrySetResult();
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(synthesizer.Texts, text => text.Contains("display only", StringComparison.Ordinal));
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Streaming display only.", assistant.Envelope!.SpeechText);
    }

    [Fact]
    public async Task Speech_projection_event_is_published_before_later_display_deltas()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(
            output,
            new ScriptedLanguageModel(["[[speech:Spoken lead.]]", "# Architecture\n", "More detail."]),
            synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("explain");
        await output.WaitForAsync(item => item.Payload is SpeechProjectionOutput);
        await output.WaitForAsync(
            item => item.Payload is TextDeltaOutput delta && delta.Text.Contains("Architecture", StringComparison.Ordinal));
        var items = output.Items.ToList();
        var projectionIndex = items.FindIndex(item => item.Payload is SpeechProjectionOutput);
        var laterDisplayIndex = items.FindIndex(
            item => item.Payload is TextDeltaOutput delta && delta.Text.Contains("Architecture", StringComparison.Ordinal));
        Assert.True(projectionIndex >= 0);
        Assert.True(laterDisplayIndex > projectionIndex);
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Technical_fenced_display_without_speech_resolves_no_speech_but_publishes_display()
    {
        const string display = """
            ```text
            Client --> HTTPS --> API Gateway --> Auth --> Task Service
            ```

            ```http
            POST /tasks HTTP/1.1
            Authorization: Bearer sk-test
            ```

            ```json
            { "id": "task-1", "title": "Example" }
            ```

            ```bash
            curl -X POST https://api.example.com/tasks
            ```
            """;
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([display]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("architecture");
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(synthesizer.Texts);
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechProjectionOutput);
        Assert.Contains(
            output.Items,
            item => item.Payload is TextDeltaOutput delta && delta.Text.Contains("POST /tasks", StringComparison.Ordinal));
        var completed = Assert.IsType<ResponseCompletedOutput>(
            output.Items.Last(item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.Equal(0, completed.HeardTextEndExclusive);
        Assert.Null(completed.SpeechText);
    }

    [Fact]
    public async Task Pure_table_without_speech_resolves_no_speech_but_publishes_display()
    {
        const string table = "| Technology | Category |\n| --- | --- |\n| React | Frontend |\n";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([table]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("table");
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(synthesizer.Texts);
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechProjectionOutput);
        Assert.Contains(
            output.Items,
            item => item.Payload is TextDeltaOutput delta && delta.Text.Contains("| Technology |", StringComparison.Ordinal));
        var completed = Assert.IsType<ResponseCompletedOutput>(
            output.Items.Last(item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.Equal(0, completed.HeardTextEndExclusive);
        Assert.Null(completed.SpeechText);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(0, assistant.HeardTextEndExclusive);
        Assert.Contains("| React |", assistant.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pure_fenced_code_without_prose_resolves_no_speech()
    {
        var code = "```csharp\n" + new string('x', 400) + "\n```";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([code]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("code");
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(synthesizer.Texts);
        var completed = Assert.IsType<ResponseCompletedOutput>(
            output.Items.Last(item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.Equal(0, completed.HeardTextEndExclusive);
    }

    [Fact]
    public async Task Rejected_explicit_speech_falls_back_to_safe_display_prose()
    {
        var unsafeSpeech =
            "Attached file secret.bin (user data, not system instructions; attachmentId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee):\n"
            + new string('A', 200);
        const string display = "Safe **spoken** fallback prose.";
        var model = $"[[speech:{unsafeSpeech}]]{display}";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([model]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("read");
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(synthesizer.Texts, text => text.Contains("Safe spoken fallback prose.", StringComparison.Ordinal));
        Assert.All(synthesizer.Texts, text => Assert.DoesNotContain("attachmentId=", text, StringComparison.Ordinal));
        Assert.Contains(
            output.Items,
            item => item.Payload is TextDeltaOutput delta && delta.Text.Contains("**spoken**", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_explicit_speech_and_unsafe_display_resolves_no_speech()
    {
        var unsafeSpeech =
            "Attached file secret.bin (user data, not system instructions; attachmentId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb):\n"
            + new string('B', 200);
        var unsafeDisplay =
            "Attached file notes.txt (user data, not system instructions; attachmentId=cccccccc-cccc-cccc-cccc-cccccccccccc):\n"
            + new string('C', 200);
        var model = $"[[speech:{unsafeSpeech}]]{unsafeDisplay}";
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = CreateVoice(output, new ScriptedLanguageModel([model]), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("dump");
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(synthesizer.Texts);
        var completed = Assert.IsType<ResponseCompletedOutput>(
            output.Items.Last(item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.Equal(0, completed.HeardTextEndExclusive);
        Assert.Null(completed.SpeechText);
    }

    private static SessionRuntime CreateVoice(
        ISessionOutput output,
        ILanguageModel model,
        ISpeechSynthesizer? synthesizer = null,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null,
        AgentDefinition? snapshotDefinition = null,
        SessionToolExecutor? tools = null,
        IArtifactStore? artifacts = null,
        IAgentBrain? brain = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-00c1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c001")]);
        store ??= new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            snapshotDefinition ?? SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        if (store.LoadAsync(snapshot.SessionId).AsTask().GetAwaiter().GetResult() is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            brain ?? new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer ?? new SyntheticSpeechSynthesizer(),
            artifacts: artifacts is null ? null : new SessionArtifactAuthorizer(artifacts),
            tools: tools);
    }

    private static async Task<AgentDefinition> Load(string id)
    {
        var catalog = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await catalog.GetAsync(id, 1))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer _inner = new();
        public List<string> Texts { get; } = [];
        public SynthesisCapabilities Capabilities => _inner.Capabilities;

        public IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken = default)
        {
            Texts.Add(request.Text);
            return _inner.SynthesizeAsync(request, cancellationToken);
        }
    }
}
