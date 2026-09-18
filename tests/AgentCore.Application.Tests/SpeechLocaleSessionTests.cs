using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SpeechLocaleSessionTests
{
    [Fact]
    public async Task Override_survives_persistence_without_rewriting_text_language()
    {
        var store = new InMemoryMemoryStore();
        var manager = CreateManager(store);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        Assert.Equal("en", created.Definition.ConversationPolicy.Language);
        Assert.Null(created.SpeechLocaleOverride);
        Assert.Equal("en", SpeechLocale.Resolve(created).Effective);
        Assert.Equal(SpeechLocaleSource.AgentDefault, SpeechLocale.Resolve(created).Source);

        var updated = await manager.SetSpeechLocaleAsync(created.SessionId, "vi-vn");
        Assert.Equal("vi-VN", updated.SpeechLocaleOverride);
        Assert.Equal("en", updated.Definition.ConversationPolicy.Language);

        var loaded = await store.LoadMetadataAsync(created.SessionId);
        Assert.Equal("vi-VN", loaded!.SpeechLocaleOverride);
        Assert.Equal("en", loaded.Definition.ConversationPolicy.Language);
        Assert.Equal("vi-VN", SpeechLocale.Resolve(loaded).Effective);
        Assert.Equal(SpeechLocaleSource.SessionOverride, SpeechLocale.Resolve(loaded).Source);
    }

    [Fact]
    public async Task Invalid_override_is_rejected_at_the_application_boundary()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.SetSpeechLocaleAsync(created.SessionId, "en_US"));
        Assert.Equal("ValidationError", error.Code);
        var loaded = await manager.GetAsync(created.SessionId);
        Assert.Null(loaded.SpeechLocaleOverride);
        Assert.Equal("en", loaded.Definition.ConversationPolicy.Language);
    }

    [Fact]
    public async Task Reconnect_ready_exposes_effective_locale_not_only_agent_language()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(store, time);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text, speechLocaleOverride: "fr-FR");
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, time, store, created);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var ready = Assert.IsType<ReadyOutput>(output.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal("en", ready.Ready.Agent.Language);
        Assert.Equal("fr-FR", ready.Ready.SpeechLocale!.Effective);
        Assert.Equal(SpeechLocaleSource.SessionOverride, ready.Ready.SpeechLocale.Source);
    }

    [Fact]
    public async Task Recognition_opens_with_the_effective_locale()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var created = await CreateManager(store, time).CreateAsync(
            "examiner",
            null,
            SessionMode.Voice,
            speechLocaleOverride: "ja-JP");
        var recognizer = new RecordingRecognizer();
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, time, store, created, recognizer);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal("ja-JP", recognizer.Language);
    }

    [Fact]
    public async Task Unsupported_locale_fails_voice_and_keeps_text()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var voice = new VoiceAvailability
        {
            SpeechAdaptersResolved = true,
            LocaleSupport = new RejectingLocaleSupport("ja-JP")
        };
        var manager = CreateManager(store, time, voice);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text, speechLocaleOverride: "ja-JP");
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, time, store, created, voice: voice);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput error && error.Code == "VoiceUnavailable");
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Still text", Guid.Parse("019944af-0000-7000-8000-000000000099")));
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
    }

    private static SessionManager CreateManager(
        IMemoryStore store,
        TimeProvider? time = null,
        VoiceAvailability? voice = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)),
            voice ?? new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        FakeTimeProvider time,
        IMemoryStore store,
        SessionSnapshot snapshot,
        ISpeechRecognizer? recognizer = null,
        VoiceAvailability? voice = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [snapshot.SessionId]);
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
            voice: voice ?? new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }

    private sealed class RejectingLocaleSupport(string blocked) : ISpeechLocaleSupport
    {
        public bool CanRecognize(string locale) => !string.Equals(locale, blocked, StringComparison.OrdinalIgnoreCase);

        public bool CanSynthesize(string locale) => !string.Equals(locale, blocked, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingRecognizer : ISpeechRecognizer
    {
        public string? Language { get; private set; }

        public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

        public ValueTask<ISpeechRecognitionSession> OpenAsync(
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Language = options.Language;
            return ValueTask.FromResult<ISpeechRecognitionSession>(new IdleSession());
        }

        private sealed class IdleSession : ISpeechRecognitionSession
        {
            public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask ObserveBoundaryAsync(
                Guid utteranceId,
                SpeechBoundary boundary,
                CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
