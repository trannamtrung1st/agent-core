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
        Assert.DoesNotContain("attachmentId=", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 80), spoken, StringComparison.Ordinal);
        Assert.True(spoken.Length <= SpokenOutput.MaxChars);
    }

    [Fact]
    public void Structured_display_uses_short_lead_in_without_explicit_speech()
    {
        var display = "```csharp\npublic class Example {}\n```\nDetails on screen.";
        Assert.Equal(SpokenOutput.StructuredLeadIn, SpokenOutput.ForPlayback(null, display));
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
    public async Task Explicit_speech_stays_short_while_blocks_remain_on_display()
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

    private static SessionRuntime CreateVoice(
        ISessionOutput output,
        ILanguageModel model,
        ISpeechSynthesizer? synthesizer = null,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null,
        AgentDefinition? snapshotDefinition = null,
        SessionToolExecutor? tools = null,
        IArtifactStore? artifacts = null)
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
            new DefaultAgentBrain(new PromptContextBuilder()),
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
