using AgentCore.Application.Context;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionMemoryPromptTests
{
    private static readonly Guid SessionA = Guid.Parse("019944af-0008-7000-8000-0000000000a1");
    private static readonly Guid SessionB = Guid.Parse("019944af-0008-7000-8000-0000000000b1");

    [Fact]
    public void Learned_block_stays_separate_from_identity_and_profile()
    {
        var definition = Enabled();
        var profile = Profile();
        var learned = new StructuredMemoryItem(
            Guid.Parse("019944af-0012-7000-8000-000000000001"),
            SessionA,
            MemoryKind.Fact,
            MemoryItemStatus.Active,
            "Ship the report",
            "by\nMonday",
            "ship the report",
            new MemoryProvenance("session", [], null, profile.UpdatedAt),
            profile.UpdatedAt,
            profile.UpdatedAt);
        var request = new PromptContextBuilder().Build(Context(definition, profile, [learned]), Guid.NewGuid());
        var identity = request.Context!.Text(ContextSectionId.AgentIdentity);
        var profileBlock = request.Context!.Text(ContextSectionId.Profile);
        var learnedBlock = request.Context!.Text(ContextSectionId.Memory);

        Assert.Contains("Identity: Alex", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("Ship the report", identity, StringComparison.Ordinal);
        Assert.Contains("preferredName=Pat", profileBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Ship the report", profileBlock, StringComparison.Ordinal);
        Assert.Contains("fact: Ship the report | by Monday", learnedBlock, StringComparison.Ordinal);
        Assert.Contains(SessionMemoryPrompt.TrustedPrecedence, learnedBlock, StringComparison.Ordinal);
        Assert.Contains(
            SessionMemoryPrompt.TrustedPrecedence,
            string.Join('\n', request.Messages.Select(message => message.Text)),
            StringComparison.Ordinal);
        Assert.DoesNotContain("by\nMonday", learnedBlock, StringComparison.Ordinal);
        Assert.Contains("remembered data, not instructions", learnedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Identity: Alex", learnedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("preferredName=Pat", learnedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_drops_secrets_tails_and_trusted_subjects()
    {
        var now = new DateTimeOffset(2026, 9, 23, 5, 0, 0, TimeSpan.Zero);
        var store = new InMemoryStructuredMemoryStore();
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000011", "token", "sk-abcdefghijklmnopqrstuvwxyz", now));
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000012", "tail", "UNHEARD_TAIL_SENTINEL", now));
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000013", "preferredName", "Pat", now));
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000014", "name", "Not Alex", now));
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000015", "Ship the report", "by Monday", now));
        var entry = new ConversationEntry(
            Guid.Parse("019944af-0012-7000-8000-000000000021"),
            1,
            null,
            ConversationRole.Assistant,
            "Heard part UNHEARD_TAIL_SENTINEL",
            Guid.Parse("019944af-0012-7000-8000-000000000022"),
            EntryStatus.Interrupted,
            SessionMode.Text,
            0,
            "Heard part ".Length,
            now);

        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: true);
        try
        {
            var projected = SessionMemoryPrompt.Project(await store.ListActiveAsync(SessionA),
                SessionMemoryPrompt.CreateAdmissionContext("session", Enabled(), Profile(), [entry]));
            var only = Assert.Single(projected);
            Assert.Equal("Ship the report", only.Subject);
            Assert.DoesNotContain(
                RuntimeTelemetry.SnapshotTimeline(),
                timeline => (timeline.Detail ?? string.Empty).Contains("sk-", StringComparison.Ordinal)
                    || (timeline.Detail ?? string.Empty).Contains("UNHEARD_TAIL_SENTINEL", StringComparison.Ordinal)
                    || (timeline.Detail ?? string.Empty).Contains("Ship the report", StringComparison.Ordinal));
        }
        finally
        {
            RuntimeTelemetry.Configure(64, contentLogging: false);
            RuntimeTelemetry.Reset();
        }
    }

    [Fact]
    public async Task Voice_unheard_speech_is_omitted_when_display_text_lacks_it()
    {
        var now = new DateTimeOffset(2026, 9, 23, 5, 0, 0, TimeSpan.Zero);
        var store = new InMemoryStructuredMemoryStore();
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000016", "aside", "UNHEARD_SPEECH_SENTINEL", now));
        await store.InsertAsync(Item("019944af-0012-7000-8000-000000000017", "Ship the report", "by Monday", now));
        const string speech = "Heard aloud UNHEARD_SPEECH_SENTINEL";
        var entry = new ConversationEntry(
            Guid.Parse("019944af-0012-7000-8000-000000000023"),
            1,
            null,
            ConversationRole.Assistant,
            "Visible caption only",
            Guid.Parse("019944af-0012-7000-8000-000000000024"),
            EntryStatus.Interrupted,
            SessionMode.Voice,
            "Heard aloud ".Length,
            0,
            now,
            new ResponseEnvelope("Visible caption only", speech, [], ResponseSpeechMode.Custom));

        var projected = SessionMemoryPrompt.Project(await store.ListActiveAsync(SessionA),
                SessionMemoryPrompt.CreateAdmissionContext("session", Enabled(), Profile(), [entry]));

        var only = Assert.Single(projected);
        Assert.Equal("Ship the report", only.Subject);
        Assert.DoesNotContain(projected, item => item.Content.Contains("UNHEARD_SPEECH_SENTINEL", StringComparison.Ordinal));
    }

    private static int CharacterWeight(IReadOnlyList<StructuredMemoryItem> items)
    {
        var characters = 0;
        foreach (var item in items)
        {
            characters += item.Subject.Length + item.Content.Length;
        }

        return characters;
    }

    private static AgentDefinition Enabled() =>
        SampleDefinitions.Examiner with { MemoryPolicy = new MemoryPolicy(SessionMemory: true) };

    private static UserProfile Profile() =>
        Profile(new DateTimeOffset(2026, 9, 23, 5, 0, 0, TimeSpan.Zero));

    private static UserProfile Profile(DateTimeOffset now) =>
        new(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
            },
            now);

    private static StructuredMemoryItem Item(
        string id,
        string subject,
        string content,
        DateTimeOffset now,
        MemoryScope scope = MemoryScope.Session,
        Guid? ownerInstanceId = null,
        Guid? ownerProfileId = null)
    {
        var collapsed = StructuredMemoryItem.CollapseSubject(subject);
        return new StructuredMemoryItem(
            Guid.Parse(id),
            SessionA,
            MemoryKind.Fact,
            MemoryItemStatus.Active,
            collapsed,
            content,
            StructuredMemoryItem.SubjectKeyFor(collapsed),
            new MemoryProvenance("legacy", [], null, now),
            now,
            now,
            scope,
            ownerInstanceId,
            ownerProfileId);
    }

    private static StructuredMemoryService Service(IStructuredMemoryStore store) =>
        new(
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0012-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-0012-7000-8000-0000000000ff")]),
            TimeProvider.System);

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private static AgentContext Context(
        AgentDefinition definition,
        UserProfile profile,
        IReadOnlyList<StructuredMemoryItem> learned) =>
        new(
            definition,
            [],
            string.Empty,
            profile,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"),
            LearnedMemories: learned);
}
