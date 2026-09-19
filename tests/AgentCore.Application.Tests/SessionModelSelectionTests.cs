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

public sealed class SessionModelSelectionTests
{
    [Fact]
    public async Task Create_without_model_pins_the_system_default()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        Assert.Equal("scripted-alpha", created.ModelSelection!.CatalogKey);
        Assert.Equal(ModelSelectionSource.SystemDefault, created.ModelSelection.SelectionSource);
        Assert.Equal("medium", created.ModelSelection.ReasoningEffort);
    }

    [Fact]
    public async Task Create_with_explicit_model_persists_that_choice()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            modelKey: "scripted-beta",
            modelSource: ModelSelectionSource.User);
        Assert.Equal("scripted-beta", created.ModelSelection!.CatalogKey);
        Assert.Equal(ModelSelectionSource.User, created.ModelSelection.SelectionSource);
        Assert.Null(created.ModelSelection.ReasoningEffort);
    }

    [Fact]
    public async Task Changing_the_system_default_does_not_rewrite_an_existing_session()
    {
        var store = new InMemoryMemoryStore();
        var created = await CreateManager(store, TestModelCatalogs.WithDefault("scripted-alpha"))
            .CreateAsync("examiner", null, SessionMode.Text);
        Assert.Equal("scripted-alpha", created.ModelSelection!.CatalogKey);

        var later = CreateManager(store, TestModelCatalogs.WithDefault("scripted-beta"));
        var loaded = await later.GetAsync(created.SessionId);
        Assert.Equal("scripted-alpha", loaded.ModelSelection!.CatalogKey);
        var fresh = await later.CreateAsync("examiner", null, SessionMode.Text);
        Assert.Equal("scripted-beta", fresh.ModelSelection!.CatalogKey);
        Assert.Equal(ModelSelectionSource.SystemDefault, fresh.ModelSelection.SelectionSource);
    }

    [Fact]
    public async Task Session_a_model_change_does_not_affect_session_b()
    {
        var store = new InMemoryMemoryStore();
        var manager = CreateManager(store);
        var first = await manager.CreateAsync("examiner", null, SessionMode.Text, modelKey: "scripted-alpha", modelSource: ModelSelectionSource.User);
        var second = await manager.CreateAsync("examiner", null, SessionMode.Text, modelKey: "scripted-alpha", modelSource: ModelSelectionSource.User);
        await manager.SetModelAsync(first.SessionId, "scripted-beta", null, ModelSelectionSource.User);
        Assert.Equal("scripted-beta", (await manager.GetAsync(first.SessionId)).ModelSelection!.CatalogKey);
        Assert.Equal("scripted-alpha", (await manager.GetAsync(second.SessionId)).ModelSelection!.CatalogKey);
    }

    [Fact]
    public async Task Terminal_sessions_cannot_change_model()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        await manager.EndAsync(created.SessionId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.SetModelAsync(created.SessionId, "scripted-beta", null, ModelSelectionSource.User));
        Assert.Equal("ValidationError", error.Code);
        var loaded = await manager.GetAsync(created.SessionId);
        Assert.Equal("scripted-alpha", loaded.ModelSelection!.CatalogKey);
    }

    [Fact]
    public async Task Attach_pins_legacy_sessions_before_generation()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var snapshot = LegacySnapshot(time.GetUtcNow());
        await store.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, time, store, snapshot, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal("scripted-alpha", runtime.Snapshot.ModelSelection!.CatalogKey);
        var durable = await store.LoadAsync(snapshot.SessionId);
        Assert.Equal("scripted-alpha", durable!.ModelSelection!.CatalogKey);
        Assert.Equal("medium", durable.ModelSelection.ReasoningEffort);
    }

    [Fact]
    public async Task Overlapping_model_change_while_persist_pending_is_rejected()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedModelSelectionStore(gate);
        var output = new CapturingSessionOutput();
        var snapshot = await CreateManager(store.Inner).CreateAsync("examiner", null, SessionMode.Text);
        await using var runtime = CreateRuntime(output, time, store, snapshot, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();

        var first = runtime.RequestModelSettingsAsync("scripted-beta", null, ModelSelectionSource.User);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            runtime.RequestModelSettingsAsync("scripted-beta", "high", ModelSelectionSource.User));
        Assert.Equal("SessionBusy", error.Code);
        gate.TrySetResult();
        Assert.True(await first);
        Assert.Equal("scripted-beta", runtime.Snapshot.ModelSelection!.CatalogKey);
    }

    [Fact]
    public async Task Live_model_change_is_not_effective_before_persistence_succeeds()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedModelSelectionStore(gate);
        var output = new CapturingSessionOutput();
        var snapshot = await CreateManager(store.Inner).CreateAsync("examiner", null, SessionMode.Text);
        await using var runtime = CreateRuntime(output, time, store, snapshot, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal("scripted-alpha", runtime.Snapshot.ModelSelection!.CatalogKey);

        var pending = runtime.RequestModelSettingsAsync("scripted-beta", null, ModelSelectionSource.User);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("scripted-alpha", runtime.Snapshot.ModelSelection.CatalogKey);
        gate.TrySetResult();
        Assert.True(await pending);
        Assert.Equal("scripted-beta", runtime.Snapshot.ModelSelection.CatalogKey);
        Assert.Equal(ModelSelectionSource.User, runtime.Snapshot.ModelSelection.SelectionSource);
    }

    [Fact]
    public async Task Failed_model_persistence_keeps_the_old_selection()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var store = new FailingModelSelectionStore();
        var output = new CapturingSessionOutput();
        var snapshot = await CreateManager(store.Inner).CreateAsync("examiner", null, SessionMode.Text);
        await using var runtime = CreateRuntime(output, time, store, snapshot, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var pending = runtime.RequestModelSettingsAsync("scripted-beta", null, ModelSelectionSource.User);
        for (var attempt = 0; attempt < 12 && !pending.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        var durable = await store.Inner.LoadAsync(snapshot.SessionId);
        Assert.Equal("scripted-alpha", durable!.ModelSelection!.CatalogKey);
    }

    [Fact]
    public async Task Model_change_while_generating_is_rejected()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = await CreateManager(store).CreateAsync("examiner", null, SessionMode.Text);
        await using var runtime = CreateRuntime(
            output,
            time,
            store,
            snapshot,
            new ScriptedLanguageModel(["Hello", " from ", "synthetic."], release));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            runtime.RequestModelSettingsAsync("scripted-beta", null, ModelSelectionSource.User));
        Assert.Equal("SessionBusy", error.Code);
        Assert.Equal("scripted-alpha", runtime.Snapshot.ModelSelection!.CatalogKey);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Next_generation_uses_the_newly_persisted_model_and_keeps_prior_provenance()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        var resolver = new RecordingResolver();
        var snapshot = await CreateManager(store).CreateAsync("examiner", null, SessionMode.Text);
        await using var runtime = CreateRuntime(
            output,
            time,
            store,
            snapshot,
            resolver.Conversation,
            resolver);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var first = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("scripted-alpha", first.ModelProvenance!.CatalogKey);
        Assert.Equal("medium", first.ModelProvenance.ReasoningEffort);
        Assert.Contains(resolver.Resolves, item => item.Purpose == ModelPurpose.Conversation);

        Assert.True(await runtime.RequestModelSettingsAsync("scripted-beta", null, ModelSelectionSource.User));
        await runtime.SubmitUserTextAsync("Again");
        await runtime.WaitUntilIdleAsync();
        var second = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("scripted-beta", second.ModelProvenance!.CatalogKey);
        Assert.Null(second.ModelProvenance.ReasoningEffort);
        Assert.Equal("scripted-alpha", first.ModelProvenance.CatalogKey);
        Assert.Equal("scripted-beta", resolver.Resolves.Last(item => item.Purpose == ModelPurpose.Conversation).Selection.CatalogKey);
    }

    [Fact]
    public async Task Conversation_initiative_and_completion_use_the_session_selection()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        var resolver = new RecordingResolver();
        var created = await CreateManager(store).CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            purpose: new SessionPurpose(SessionPurposeKind.Goal, "Collect a spoken sample"),
            policy: new SessionCompletionPolicy(AgentCompletionAuthority.Advisory, true, true),
            modelKey: "scripted-alpha",
            modelSource: ModelSelectionSource.User);
        await using var runtime = CreateRuntime(output, time, store, created, resolver.Conversation, resolver);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(resolver.Resolves, item => item.Purpose == ModelPurpose.Conversation);
        Assert.Contains(resolver.Resolves, item => item.Purpose == ModelPurpose.CompletionEvaluation);
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(resolver.Resolves, item => item.Purpose == ModelPurpose.Initiative);
        Assert.All(resolver.Resolves, item => Assert.Equal("scripted-alpha", item.Selection.CatalogKey));
    }

    private static SessionManager CreateManager(IMemoryStore store, IModelCatalog? catalog = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(_ => Guid.NewGuid()).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero)),
            new VoiceAvailability { SpeechAdaptersResolved = true },
            models: catalog ?? TestModelCatalogs.Synthetic());
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        FakeTimeProvider time,
        IMemoryStore store,
        SessionSnapshot snapshot,
        ILanguageModel model,
        ILanguageModelResolver? resolver = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [snapshot.SessionId]);
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
            voice: new VoiceAvailability { SpeechAdaptersResolved = true },
            modelResolver: resolver,
            catalog: TestModelCatalogs.Synthetic());
    }

    private static SessionSnapshot LegacySnapshot(DateTimeOffset now) =>
        new(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
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

    private sealed class RecordingResolver : ILanguageModelResolver
    {
        public RecordingLanguageModel Conversation { get; } = new(new ScriptedLanguageModel());

        public List<(SessionModelSelection Selection, ModelPurpose Purpose)> Resolves { get; } = [];

        public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose)
        {
            Resolves.Add((selection, purpose));
            return Conversation;
        }
    }

    private sealed class GatedModelSelectionStore(TaskCompletionSource gate) : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.ModelSelection?.CatalogKey == "scripted-beta")
            {
                SaveStarted.TrySetResult();
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

        public ValueTask<SessionCatalogPage> ListCatalogAsync(
            string? cursor,
            int limit,
            bool includeArchived,
            CancellationToken cancellationToken = default) =>
            Inner.ListCatalogAsync(cursor, limit, includeArchived, cancellationToken);
    }

    private sealed class FailingModelSelectionStore : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.ModelSelection?.CatalogKey == "scripted-beta")
            {
                throw AgentCoreErrors.Persistence("forced model persist failure");
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

        public ValueTask<SessionCatalogPage> ListCatalogAsync(
            string? cursor,
            int limit,
            bool includeArchived,
            CancellationToken cancellationToken = default) =>
            Inner.ListCatalogAsync(cursor, limit, includeArchived, cancellationToken);
    }
}
