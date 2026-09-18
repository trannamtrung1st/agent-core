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

public sealed class ClientTranscriptAdmissionTests
{
    [Fact]
    public async Task Voice_mode_listens_without_a_recognition_session()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, recognizer: new SyntheticSpeechRecognizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.Equal(InputActivity.Listening, runtime.Input);
        Assert.False(runtime.RecognitionActive);
        Assert.NotNull(runtime.StreamId);
        Assert.False(runtime.TryAdmitAudio(new AudioFrame(1, 0, new byte[960])));
    }

    [Fact]
    public async Task One_durable_final_final_before_ended_and_ephemeral_partials()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-00d0-7000-8000-000000000001");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.8);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "hel", 0.7), 0.8);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "hel", 0.7), 0.8);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 0, "stale", 0.7), 0.8);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "hello there", 0.9), 0.8);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User && entry.Text == "hello there"));
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text == "hel" || entry.Text == "stale");
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.2);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "hello there", 0.9), 0.8);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
        Assert.Contains(output.Items, item => item.Payload is TranscriptPartialOutput);
        Assert.Single(output.Items.Select(item => item.Payload).OfType<TranscriptFinalOutput>());
    }

    [Fact]
    public async Task Failed_kind_does_not_create_a_user_turn()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-00d0-7000-8000-000000000002");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.8);
        await runtime.SubmitSpeechAsync(
            new RecognitionFailed(utterance, new ProviderFailure(ProviderErrorCode.Unknown, "Client transcript failed.")),
            0.1);
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
    }

    [Fact]
    public async Task Mute_ignores_client_transcript_evidence()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SetMutedAsync(true);
        await runtime.WaitUntilMailboxDrainedAsync();
        var utterance = Guid.Parse("019944af-00d0-7000-8000-000000000003");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "secret", 0.9), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text == "secret");
        Assert.False(runtime.CanAdmitClientTranscriptEvidence());
    }

    private static SessionRuntime Create(ISessionOutput output, ISpeechRecognizer? recognizer = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00d1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940d001")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
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
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: recognizer,
            voice: new VoiceAvailability
            {
                SpeechAdaptersResolved = true,
                Plan = new EffectiveSpeechPlan(
                    SpeechTransport.ClientTranscript,
                    SpeechTransport.ServerAudio,
                    true,
                    true,
                    null,
                    null)
            });
    }
}
