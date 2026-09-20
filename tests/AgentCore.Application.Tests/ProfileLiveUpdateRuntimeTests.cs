using AgentCore.Application.Agents;
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

public sealed class ProfileLiveUpdateRuntimeTests
{
    [Fact]
    public async Task ApplyProfileAsync_ignores_mismatched_profile_id_and_equal_revision()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var (runtime, model) = await CreateAttachedRuntimeAsync(store, time);
        await using (runtime)
        {
            var current = (await store.LoadProfileAsync(LocalUserProfile.Id))!;
            await runtime.ApplyProfileAsync(current);
            await runtime.ApplyProfileAsync(current with { ProfileId = Guid.NewGuid(), Revision = current.Revision + 5 });

            var updated = current with
            {
                Revision = current.Revision + 1,
                Preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                {
                    ["preferredName"] = new UserProfileValue("Sam", UserProfileValueSource.UserSet, time.GetUtcNow())
                }
            };
            await runtime.ApplyProfileAsync(updated);

            await runtime.SubmitUserTextAsync("Hello");
            await runtime.WaitUntilIdleAsync();
            Assert.Contains("preferredName=Sam", model.LastRequest!.Messages[2].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Update_notifies_two_live_runtimes_on_subsequent_turns()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var notifier = new TestProfileLiveUpdateNotifier();
        var profiles = new LocalUserProfileService(store, time, notifier);
        var manager = CreateSessionManager(store, time, profiles);
        var snapshotA = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var snapshotB = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var (runtimeA, modelA) = await AttachRuntimeAsync(snapshotA, store, time);
        var (runtimeB, modelB) = await AttachRuntimeAsync(snapshotB, store, time);
        await using (runtimeA)
        await using (runtimeB)
        {
            notifier.Register(runtimeA);
            notifier.Register(runtimeB);

            var current = await profiles.GetLocalProfileAsync();
            await profiles.UpdateLocalProfileAsync(
                current.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["preferredName"] = "Sam" },
                UserProfileValueSource.UserSet);

            await runtimeA.SubmitUserTextAsync("Hi A");
            await runtimeB.SubmitUserTextAsync("Hi B");
            await Task.WhenAll(runtimeA.WaitUntilIdleAsync(), runtimeB.WaitUntilIdleAsync());

            Assert.Contains("preferredName=Sam", modelA.LastRequest!.Messages[2].Text, StringComparison.Ordinal);
            Assert.Contains("preferredName=Sam", modelB.LastRequest!.Messages[2].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Profile_update_does_not_change_session_revision_or_speech_locale_override()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var profiles = new LocalUserProfileService(store, time);
        var (runtime, _) = await CreateAttachedRuntimeAsync(store, time, profiles, speechLocaleOverride: "fr-FR");
        await using (runtime)
        {
            var revisionBefore = runtime.Snapshot.Revision;
            var current = await profiles.GetLocalProfileAsync();
            await runtime.ApplyProfileAsync(current with
            {
                Revision = current.Revision + 1,
                Preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                {
                    ["language"] = new UserProfileValue("de", UserProfileValueSource.UserSet, time.GetUtcNow()),
                    ["locale"] = new UserProfileValue("de-DE", UserProfileValueSource.UserSet, time.GetUtcNow())
                }
            });

            Assert.Equal(revisionBefore, runtime.Snapshot.Revision);
            Assert.Equal("fr-FR", runtime.Snapshot.SpeechLocaleOverride);
        }
    }

    private static async Task<(SessionRuntime Runtime, RecordingLanguageModel Model)> CreateAttachedRuntimeAsync(
        IMemoryStore store,
        FakeTimeProvider time,
        ILocalUserProfileService? profiles = null,
        string? speechLocaleOverride = null)
    {
        profiles ??= new LocalUserProfileService(store, time);
        var manager = CreateSessionManager(store, time, profiles);
        var snapshot = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            speechLocaleOverride: speechLocaleOverride);
        return await AttachRuntimeAsync(snapshot, store, time);
    }

    private static SessionManager CreateSessionManager(
        IMemoryStore store,
        FakeTimeProvider time,
        ILocalUserProfileService profiles)
    {
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0008-7000-8000-{index:D12}")),
                [
                    Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b848"),
                    Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b849"),
                    Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b84a")
                ]),
            time,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            localProfiles: profiles);
    }

    private static async Task<(SessionRuntime Runtime, RecordingLanguageModel Model)> AttachRuntimeAsync(
        SessionSnapshot snapshot,
        IMemoryStore store,
        FakeTimeProvider time)
    {
        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b848")]);
        var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        return (runtime, model);
    }

    private sealed class TestProfileLiveUpdateNotifier : IProfileLiveUpdateNotifier
    {
        private readonly List<SessionRuntime> _runtimes = [];

        public void Register(SessionRuntime runtime) => _runtimes.Add(runtime);

        public async ValueTask NotifyProfileUpdatedAsync(UserProfile profile, CancellationToken cancellationToken = default)
        {
            foreach (var runtime in _runtimes)
            {
                await runtime.ApplyProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
