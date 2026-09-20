using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class PreferredNameProvenanceTests
{
    [Fact]
    public void Memory_prompt_omits_invented_friend_and_does_not_invite_a_name()
    {
        var builder = new PromptContextBuilder();
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var seeded = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = LocalUserProfile.ApplicationProfileValue("friend", now)
            },
            now);
        var sections = builder.BuildSections(Context(seeded));
        Assert.DoesNotContain("friend", sections.MemorySystem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preferredName=", sections.MemorySystem, StringComparison.Ordinal);
        Assert.Contains("language=en", sections.MemorySystem, StringComparison.Ordinal);
        Assert.Contains("No preferred user name or form of address is known. Do not invent one.", sections.MemorySystem, StringComparison.Ordinal);
        Assert.DoesNotContain("friend", sections.IdentitySystem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Memory_prompt_keeps_a_supplied_preferred_name()
    {
        var builder = new PromptContextBuilder();
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var profile = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
            },
            now);
        var sections = builder.BuildSections(Context(profile));
        Assert.Contains("preferredName=Pat", sections.MemorySystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not invent one.", sections.MemorySystem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_does_not_seed_friend_and_created_session_prompt_lacks_invented_name()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var manager = CreateManager(store, time);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var profile = await store.LoadProfileAsync(LocalUserProfile.Id);
        Assert.NotNull(profile);
        Assert.False(profile!.Preferences.ContainsKey("preferredName"));

        var model = new RecordingLanguageModel(new Infrastructure.Providers.Synthetic.ScriptedLanguageModel());
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0008-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b848")]);
        await using var runtime = new SessionRuntime(
            created,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.NotNull(model.LastRequest);
        var memory = model.LastRequest!.Messages[2].Text;
        Assert.DoesNotContain("friend", memory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preferredName=", memory, StringComparison.Ordinal);
        Assert.Contains("Do not invent one.", memory, StringComparison.Ordinal);
        Assert.Contains(model.LastRequest.Messages, message => message.Role == ModelRole.User && message.Text == "Hello");
    }

    [Fact]
    public async Task Ensure_strips_durable_seeded_friend_before_prompt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        await harness.Store.SaveProfileAsync(
            new UserProfile(
                LocalUserProfile.Id,
                1,
                LocalUserProfile.CreateDefaultSeed(time.GetUtcNow()),
                time.GetUtcNow()),
            0);
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.Profiles.SingleAsync(item => item.ProfileId == LocalUserProfile.Id.ToString("D"));
            row.PreferencesJson = """{"language":"en","preferredName":"friend"}""";
            await db.SaveChangesAsync();
        }

        var manager = CreateManager(harness.Store, time);
        await manager.CreateAsync("examiner", null, SessionMode.Text);
        var profile = await harness.Store.LoadProfileAsync(LocalUserProfile.Id);
        Assert.NotNull(profile);
        Assert.False(profile!.Preferences.ContainsKey("preferredName"));
        Assert.Equal("en", profile.Preferences["language"].Value);
    }

    private static AgentContext Context(UserProfile profile)
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var user = new ConversationEntry(
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            1,
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            ConversationRole.User,
            "Hello",
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            5,
            5,
            now);
        return new AgentContext(
            SampleDefinitions.Examiner,
            [user],
            string.Empty,
            profile,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(user.EntryId, TriggerKind.UserTurn, "Hello"));
    }

    private static SessionManager CreateManager(IMemoryStore store, FakeTimeProvider time)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0009-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b9{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            time,
            new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class StaticDefinitions(AgentCore.Domain.Definitions.AgentDefinition definition)
        : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentCore.Domain.Definitions.AgentDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentCore.Domain.Definitions.AgentDefinition>>([definition]);

        public ValueTask<AgentCore.Domain.Definitions.AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentCore.Domain.Definitions.AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
