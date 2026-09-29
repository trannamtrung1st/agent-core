using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class MemoryProposalAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceA = Guid.Parse("019944af-0030-7000-8000-0000000000a1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-0030-7000-8000-0000000000a2");
    private static readonly Guid SessionA = Guid.Parse("019944af-0030-7000-8000-0000000000b1");
    private static readonly Guid SessionB = Guid.Parse("019944af-0030-7000-8000-0000000000b2");
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Fact]
    public async Task Natural_wording_without_remember_stores_the_agent_proposal()
    {
        Assert.False(ExplicitUserMemoryRequests.TryParse("I prefer concise answers.", out _));
        var memories = Service();
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(memoryTurns:
            [
                [new MemoryProposal(
                    MemoryProposalOperation.Upsert,
                    MemoryKind.Preference,
                    "answer length",
                    "concise",
                    null,
                    MemoryProposalSource.AgentInferred)]
            ]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "I prefer concise answers.",
            Guid.Parse("019944af-0030-7000-8000-000000000011")));
        await runtime.WaitUntilIdleAsync();

        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        var item = Assert.Single(stored);
        Assert.Equal(MemoryKind.Preference, item.Kind);
        Assert.Equal("concise", item.Content);
        Assert.Equal("agent_inferred", item.Provenance.Source);
        Assert.Contains("Memory saved for later sessions: answer length.", AssistantText(runtime), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wording_outside_the_retired_parser_still_stores_a_proposal()
    {
        const string text = "Remind yourself that I don't want verbose explanations.";
        Assert.False(ExplicitUserMemoryRequests.TryParse(text, out _));
        var memories = Service();
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(memoryTurns:
            [
                [Proposal(MemoryKind.Preference, "explanation length", "not verbose", MemoryProposalSource.UserExplicit)]
            ]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(text, Guid.Parse("019944af-0030-7000-8000-000000000012")));
        await runtime.WaitUntilIdleAsync();
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("user_explicit"));
        Assert.Equal("not verbose", Assert.Single(stored).Content);
        Assert.Equal("user_explicit", stored[0].Provenance.Source);
    }

    [Fact]
    public async Task Same_instance_and_profile_recalls_promoted_memory_in_a_new_session()
    {
        var memories = Service();
        var definition = Enabled();
        await using (var runtime = await RuntimeAsync(
            memories,
            definition,
            new ScriptedLanguageModel(memoryTurns: [[Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred)]])))
        {
            Assert.True(await runtime.SubmitPersistedUserTextAsync(
                "We decided the project codename is Atlas.",
                Guid.Parse("019944af-0030-7000-8000-000000000013")));
            await runtime.WaitUntilIdleAsync();
        }

        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(Profile(), 0);
        await sessions.SaveAsync(Snapshot(SessionB, definition), 0);
        var recall = new RecordingModel(new ScriptedLanguageModel());
        await using var second = Runtime((await sessions.LoadAsync(SessionB))!, sessions, memories, recall);
        await second.AttachAsync();
        Assert.True(await second.SubmitPersistedUserTextAsync(
            "What is the project codename?",
            Guid.Parse("019944af-0030-7000-8000-000000000014")));
        await second.WaitUntilIdleAsync();
        var learned = recall.LastRequest!.Messages.Single(message =>
            message.Text.StartsWith(SessionMemoryPrompt.LearnedDataLabel, StringComparison.Ordinal)).Text;
        Assert.Contains("Atlas", learned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correction_updates_the_same_subject_without_a_memory_id()
    {
        var memories = Service();
        var definition = Enabled();
        var logger = NullLogger.Instance;
        Assert.Equal(
            MemoryAdmissionStatus.Stored,
            await MemoryAdmission.AdmitOneAsync(memories, definition, SessionA, InstanceA, Profile(), [], Guid.NewGuid(), Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred), logger));
        Assert.Equal(
            MemoryAdmissionStatus.Updated,
            await MemoryAdmission.AdmitOneAsync(memories, definition, SessionA, InstanceA, Profile(), [], Guid.NewGuid(), Proposal(MemoryKind.Fact, "project codename", "Borealis", MemoryProposalSource.AgentInferred), logger));
        var identity = await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery("codename", null),
            retrievalAllowed: true,
            Admission("agent_inferred"));
        Assert.Contains(identity, item => item.Content == "Borealis");
        Assert.DoesNotContain(identity, item => item.Content == "Atlas");
    }

    [Fact]
    public async Task Repeating_the_same_proposal_is_already_stored()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred);
        var logger = NullLogger.Instance;
        Assert.Equal(MemoryAdmissionStatus.Stored, await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, logger));
        Assert.Equal(MemoryAdmissionStatus.AlreadyStored, await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, logger));
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        Assert.Single(stored);
    }

    [Fact]
    public async Task Disabled_memory_rejects_the_proposal_and_does_not_claim_a_save()
    {
        var memories = Service();
        var definition = SampleDefinitions.Support with { MemoryPolicy = MemoryPolicy.Disabled };
        await using var runtime = await RuntimeAsync(
            memories,
            definition,
            new ScriptedLanguageModel(memoryTurns: [[Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred)]]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "My favorite editor is Rider.",
            Guid.Parse("019944af-0030-7000-8000-000000000015")));
        await runtime.WaitUntilIdleAsync();
        Assert.Contains("Memory was not saved: editor.", AssistantText(runtime), StringComparison.Ordinal);
        Assert.DoesNotContain("Memory saved", AssistantText(runtime), StringComparison.Ordinal);
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
    }

    [Fact]
    public async Task Session_scope_hint_stays_inside_the_session()
    {
        var memories = Service();
        var proposal = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Fact,
            "scratch note",
            "today only",
            MemoryScopeHint.Session,
            MemoryProposalSource.AgentInferred);
        Assert.Equal(
            MemoryAdmissionStatus.StoredSessionOnly,
            await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, NullLogger.Instance));
        Assert.Single(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
        Assert.Empty(await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred")));
    }

    [Fact]
    public async Task Sensitive_content_is_rejected_and_not_described_as_saved()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "token", "sk-abcdefghijklmnopqrst", MemoryProposalSource.UserExplicit);
        Assert.Equal(
            MemoryAdmissionStatus.Rejected,
            await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, NullLogger.Instance));
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("user_explicit")));
        Assert.Equal("Memory was not saved: token.", MemoryAdmissionPrompt.Render(MemoryAdmissionStatus.Rejected, "token"));
    }

    [Fact]
    public async Task Another_instance_or_profile_cannot_read_the_promoted_memory()
    {
        var memories = Service();
        await MemoryAdmission.AdmitOneAsync(
            memories,
            Enabled(),
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.NewGuid(),
            Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred),
            NullLogger.Instance);
        var otherInstance = await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceB, ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred"));
        Assert.Empty(otherInstance);
        var otherProfile = new UserProfile(Guid.Parse("019944af-0030-7000-8000-0000000000c9"), 1, new Dictionary<string, UserProfileValue>(), Now);
        Assert.Empty(await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, otherProfile.ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred")));
    }

    [Fact]
    public async Task Application_command_uses_the_same_admission_seam()
    {
        var memories = Service();
        var proposal = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Decision,
            "production database",
            "PostgreSQL",
            null,
            MemoryProposalSource.Application);
        var status = await MemoryAdmission.AdmitOneAsync(
            memories,
            Enabled(),
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.Empty,
            proposal,
            NullLogger.Instance);
        Assert.Equal(MemoryAdmissionStatus.Stored, status);
        var stored = Assert.Single(await memories.SearchAsync(
            new TrustedMemoryOwner(SessionA),
            new MemorySearchQuery(null, null),
            Admission("application")));
        Assert.Equal("application", stored.Provenance.Source);
        Assert.Equal("PostgreSQL", stored.Content);
    }

    private static string AssistantText(SessionRuntime runtime) =>
        runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Text;

    private static MemoryProposal Proposal(MemoryKind kind, string subject, string content, MemoryProposalSource source) =>
        new(MemoryProposalOperation.Upsert, kind, subject, content, null, source);

    private static MemoryAdmissionContext Admission(string source) =>
        new(source, [], new HashSet<string>(StringComparer.Ordinal));

    private static StructuredMemoryService Service() =>
        new(new InMemoryStructuredMemoryStore(), new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0031-7000-8000-{index:D12}")),
            [Guid.Parse("019944af-0030-7000-8000-0000000000ff")]), new FakeTimeProvider(Now));

    private static async Task<SessionRuntime> RuntimeAsync(
        StructuredMemoryService memories,
        AgentDefinition definition,
        ILanguageModel model)
    {
        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(Profile(), 0);
        await sessions.SaveAsync(Snapshot(SessionA, definition), 0);
        var runtime = Runtime((await sessions.LoadAsync(SessionA))!, sessions, memories, model);
        await runtime.AttachAsync();
        return runtime;
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore sessions,
        IStructuredMemoryService memories,
        ILanguageModel model) =>
        new(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            new DeterministicIdGenerator(
                Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0032-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-0030-7000-8000-0000000000fe")]),
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: memories);

    private static AgentDefinition Enabled() =>
        SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: true,
                IdentityUserPromotion: true,
                IdentityUserRetrieval: true)
        };

    private static UserProfile Profile() =>
        new(ProfileId, 1, new Dictionary<string, UserProfileValue>(), Now);

    private static SessionSnapshot Snapshot(Guid sessionId, AgentDefinition definition) =>
        new(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: sessionId == SessionB ? InstanceA : InstanceA);

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelRequest? LastRequest { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
