using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionRuntimeLifecycleRoutingTests
{
    [Fact]
    public async Task Live_paused_to_active_does_not_terminalize_and_accepts_input()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel(), store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, runtime.Snapshot.Entries.Count);

        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        Assert.Equal(SessionLifecycleStatus.Paused, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.NotEqual(SessionStatus.Ended, runtime.Snapshot.Status);
        Assert.Equal(LifecycleTransitionSource.User, runtime.Snapshot.LifecycleSource);

        Assert.False(await runtime.SubmitPersistedUserTextAsync(
            "While paused",
            Guid.Parse("019944af-0000-7000-8000-000000000091")));

        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "resume"));
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.NotEqual(SessionStatus.Ended, runtime.Snapshot.Status);
        Assert.NotEqual(SessionStatus.Ending, runtime.Snapshot.Status);

        await runtime.SubmitUserTextAsync("Again");
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Text == "Again");
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        var durable = await store.LoadAsync(runtime.SessionId);
        Assert.Equal(SessionLifecycleStatus.Active, durable!.LifecycleStatus);
        Assert.NotEqual(SessionStatus.Ended, durable.Status);
    }

    [Fact]
    public async Task Live_active_to_active_is_idempotent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        var before = runtime.Snapshot;
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "noop"));
        Assert.Equal(before.Revision, runtime.Snapshot.Revision);
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
    }

    [Fact]
    public async Task Live_terminal_to_active_is_rejected()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Completed,
            LifecycleTransitionSource.Host,
            "done"));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            runtime.RequestLifecycleTransitionAsync(
                SessionLifecycleStatus.Active,
                LifecycleTransitionSource.Host,
                "reopen"));
        Assert.Equal("ValidationError", error.Code);
        Assert.Equal(SessionLifecycleStatus.Completed, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Ended, runtime.Snapshot.Status);
    }

    [Theory]
    [InlineData(SessionLifecycleStatus.Completed)]
    [InlineData(SessionLifecycleStatus.Expired)]
    [InlineData(SessionLifecycleStatus.Cancelled)]
    [InlineData(SessionLifecycleStatus.Ended)]
    public async Task Live_paused_terminalizes_for_each_terminal_target(SessionLifecycleStatus target)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel(), store);
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            target,
            LifecycleTransitionSource.Host,
            "close"));
        Assert.Equal(target, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Ended, runtime.Snapshot.Status);
        var durable = await store.LoadAsync(runtime.SessionId);
        Assert.Equal(target, durable!.LifecycleStatus);
        Assert.Equal(SessionStatus.Ended, durable.Status);
    }

    [Fact]
    public async Task Live_pause_preserves_requested_provenance()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.Host,
            "proctor-hold"));
        Assert.Equal(LifecycleTransitionSource.Host, runtime.Snapshot.LifecycleSource);
        Assert.Equal("proctor-hold", runtime.Snapshot.LifecycleReason);
        Assert.NotNull(runtime.Snapshot.LifecycleChangedAt);
    }

    [Fact]
    public async Task Live_voice_pause_resume_restarts_recognition()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var recognizer = new SyntheticSpeechRecognizer();
        await using var runtime = CreateVoice(output, time, recognizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.True(runtime.RecognitionActive);

        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.False(runtime.RecognitionActive);

        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "resume"));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(runtime.RecognitionActive);
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.Mode);
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Live_resume_persist_pending_rejects_user_text()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ResumeGatedStore(gate);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel(), store);
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        await runtime.WaitUntilMailboxDrainedAsync();

        var resume = runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "resume");
        await store.ResumeSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal(SessionLifecycleStatus.Paused, runtime.Snapshot.LifecycleStatus);
        Assert.False(await runtime.SubmitPersistedUserTextAsync(
            "while resume persists",
            Guid.Parse("019944af-0000-7000-8000-000000000092")));

        gate.TrySetResult();
        Assert.True(await resume.WaitAsync(TimeSpan.FromSeconds(5)));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
    }

    [Fact]
    public async Task Live_resume_active_persist_failure_keeps_paused_durable_state()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var store = new FailingActiveResumeStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel(), store);
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        await runtime.WaitUntilMailboxDrainedAsync();
        var durableAfterPause = await store.Inner.LoadAsync(runtime.SessionId);
        Assert.Equal(SessionLifecycleStatus.Paused, durableAfterPause!.LifecycleStatus);
        Assert.Equal(SessionStatus.Paused, durableAfterPause.Status);

        var resume = runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "resume");
        for (var attempt = 0; attempt < 12 && !resume.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.False(await resume);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionLifecycleStatus.Paused, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        var durableAfterFailure = await store.Inner.LoadAsync(runtime.SessionId);
        Assert.Equal(SessionLifecycleStatus.Paused, durableAfterFailure!.LifecycleStatus);
        Assert.Equal(SessionStatus.Paused, durableAfterFailure.Status);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput error && error.Code == "SessionPersistenceUnavailable");
    }

    [Fact]
    public async Task Request_deactivate_uses_user_manual_lifecycle_pause()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, time, new ScriptedLanguageModel(), store);
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestDeactivateAsync());
        Assert.Equal(SessionLifecycleStatus.Paused, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal(LifecycleTransitionSource.User, runtime.Snapshot.LifecycleSource);
        Assert.Equal("manual", runtime.Snapshot.LifecycleReason);
        var durable = await store.LoadAsync(runtime.SessionId);
        Assert.Equal(LifecycleTransitionSource.User, durable!.LifecycleSource);
        Assert.Equal("manual", durable.LifecycleReason);
    }

    private static SessionRuntime CreateVoice(
        ISessionOutput output,
        FakeTimeProvider time,
        ISpeechRecognizer recognizer)
    {
        var store = new InMemoryMemoryStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
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
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            voice: new VoiceAvailability { SpeechAdaptersResolved = true },
            recognizer: recognizer,
            synthesizer: new SyntheticSpeechSynthesizer());
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        FakeTimeProvider time,
        ILanguageModel model,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null)
    {
        store ??= new InMemoryMemoryStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
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
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            voice: new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class ResumeGatedStore(TaskCompletionSource gate) : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();
        public TaskCompletionSource ResumeSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pausedPersisted;

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.LifecycleStatus == SessionLifecycleStatus.Paused)
            {
                _pausedPersisted = true;
            }
            else if (_pausedPersisted
                     && snapshot.LifecycleStatus == SessionLifecycleStatus.Active
                     && snapshot.Status == SessionStatus.Attached)
            {
                ResumeSaveStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await Inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            Inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            Inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class FailingActiveResumeStore : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();
        private bool _pausedPersisted;

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.LifecycleStatus == SessionLifecycleStatus.Paused)
            {
                _pausedPersisted = true;
            }
            else if (_pausedPersisted
                     && snapshot.LifecycleStatus == SessionLifecycleStatus.Active
                     && snapshot.Status is SessionStatus.Attached or SessionStatus.Created)
            {
                throw AgentCoreErrors.Persistence("forced active resume persist failure");
            }

            return Inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            Inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            Inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}
