using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class DefinitionLifecycleSessionSnapshotTests
{
    [Fact]
    public async Task Later_publication_and_deprecation_do_not_rewrite_stored_session_snapshot()
    {
        var memory = new InMemoryMemoryStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var sessionId = Guid.Parse("019944af-00d1-7000-8000-0000000000aa");
        var pinned = new AgentIdentity("Pinned", "Examiner", "Practice speaking.", "Supportive");
        var definition = SampleDefinition("examiner", 1, pinned, "Built-in instructions v1.");
        var snapshot = new SessionSnapshot(
            SchemaVersion: 1,
            SessionId: sessionId,
            Revision: 1,
            Definition: definition,
            Mode: SessionMode.Text,
            PendingMode: null,
            Status: SessionStatus.Attached,
            Entries: [],
            Summary: string.Empty,
            SummarizedThroughEntrySequence: 0,
            PendingTopic: null,
            ProfileId: null,
            CreatedAt: now,
            UpdatedAt: now,
            PinnedPersona: pinned);
        await memory.SaveAsync(snapshot, 0);

        var candidate = AgentDefinitionCandidate.FromDefinition(definition) with
        {
            SystemInstructions = "Durable publication body must not leak into snapshots."
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate("examiner", candidate, DefinitionDraftSourceKind.ForkBuiltIn, 1, now),
            CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [1], now.AddMinutes(1)),
            CancellationToken.None);
        await admin.DeprecatePublicationAsync(
            new AgentDefinitionPublicationDeprecate("examiner", published.Version, 1, now.AddMinutes(2)),
            CancellationToken.None);

        var reloaded = await memory.LoadAsync(sessionId);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded!.Definition.Version);
        Assert.Equal("Built-in instructions v1.", reloaded.Definition.SystemInstructions);
        Assert.Equal(pinned, reloaded.PinnedPersona);
        Assert.Equal(pinned, reloaded.Definition.Identity);
    }

    private static AgentDefinition SampleDefinition(
        string id,
        int version,
        AgentIdentity identity,
        string instructions) =>
        new(
            1,
            id,
            version,
            identity,
            ["goal"],
            instructions,
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());
}
