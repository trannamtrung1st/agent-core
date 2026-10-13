using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SpeechLocaleRuntimeTests
{
    [Fact]
    public async Task Locale_selection_reaches_generation_and_tts_without_changing_text_language()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var ids = new DeterministicIdGenerator(Enumerable.Range(1, 256).Select(i =>
            Guid.Parse($"019944af-0000-7000-8000-{i:D12}")), [Guid.NewGuid()]);
        var store = new InMemoryMemoryStore();
        var snapshot = new SessionSnapshot(1, ids.NewSessionId(), 1, SampleDefinitions.Examiner,
            SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null,
            time.GetUtcNow(), time.GetUtcNow(), AgentInstanceId: Guid.NewGuid());
        await store.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        var model = new RecordingModel();
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = SessionRuntimeFixture.Create(snapshot, model,
            new DefaultAgentBrain(new PromptContextBuilder()), store, output, ids, time,
            NullLogger<SessionRuntime>.Instance, recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer);
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestSpeechLocaleAsync("vi-VN"));
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);

        foreach (var (locale, spoken) in new[]
        {
            ("vi-VN", "Xin chào."), ("fr-FR", "Bonjour."), ("en", "Hello.")
        })
        {
            if (locale != "vi-VN")
                Assert.True(await runtime.RequestSpeechLocaleAsync(locale == "en" ? null : locale));
            model.NextSpeech = spoken;
            await runtime.SubmitUserTextAsync("Hello");
            var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput { IsFinal: true }
                && item.ResponseId == runtime.ActiveResponseId);
            var request = model.Requests[^1];
            var mode = Assert.Single(request.Messages, m => m.Text.StartsWith("Current session mode:"));
            Assert.Contains($"Spoken response language: {locale}.", mode.Text);
            Assert.Contains("Respond in en.", request.Messages[0].Text);
            Assert.True(request.ResponseContract!.SpeechWillBeUsed);
            Assert.Equal(locale, synthesizer.Requests[^1].Language);
            Assert.Equal(spoken, synthesizer.Requests[^1].Text);
            await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
            await runtime.WaitUntilIdleAsync();
            var assistant = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.Equal(EntryStatus.Completed, assistant.Status);
            Assert.Equal("Hello on screen.", assistant.Text);
            Assert.Equal(spoken, assistant.Envelope!.SpeechText);
        }

        Assert.Null(runtime.Snapshot.SpeechLocaleOverride);
        Assert.Equal("en", runtime.Snapshot.Definition.ConversationPolicy.Language);
        await runtime.SetModeAsync(SessionMode.Text);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Text);
        Assert.True(await runtime.RequestSpeechLocaleAsync("vi-VN"));
        await runtime.SubmitUserTextAsync("Still text");
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(model.Requests[^1].Messages, m => m.Text.Contains("Spoken response language:"));
        Assert.Equal(3, synthesizer.Requests.Count);
        Assert.Equal("Hello on screen.", runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text);
    }

    private sealed class RecordingModel : ILanguageModel
    {
        public string NextSpeech { get; set; } = "";
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ModelSemanticResponseReady(new("Hello on screen.",
                new(ModelSpeechMode.Custom, NextSpeech), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer _inner = new();
        public List<SpeechRequest> Requests { get; } = [];
        public SynthesisCapabilities Capabilities => _inner.Capabilities;
        public IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(SpeechRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _inner.SynthesizeAsync(request, cancellationToken);
        }
    }
}
