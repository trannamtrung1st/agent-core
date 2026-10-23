using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class MemoryProposalAdmissionTests
{
    [Theory]
    [InlineData(MemoryProposalSource.UserExplicit)]
    [InlineData(MemoryProposalSource.AgentInferred)]
    public async Task Unowned_inline_proposals_report_unavailable_without_a_global_memory_dependency(MemoryProposalSource source)
    {
        var model = new ScriptedLanguageModel(["Noted."], memoryTurns:
            [[Proposal(MemoryKind.Fact, "Sample project", "Use the Atlas example project.", source)]]);
        await using var runtime = await RuntimeAsync(Enabled(), model);
        await runtime.SubmitPersistedUserTextAsync("Remember the Atlas example project.", Guid.NewGuid());
        await runtime.WaitUntilIdleAsync();
        Assert.Equal("unavailable", AssistantReceipt(runtime).Outcome);
        Assert.DoesNotContain(typeof(SessionRuntime).GetConstructors().SelectMany(c => c.GetParameters()),
            parameter => parameter.ParameterType.FullName == "AgentCore.Application.Ports.IStructuredMemoryService");
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceA = Guid.Parse("019944af-0030-7000-8000-0000000000a1");
    private static readonly Guid SessionA = Guid.Parse("019944af-0030-7000-8000-0000000000b1");
    private static readonly Guid SessionB = Guid.Parse("019944af-0030-7000-8000-0000000000b2");
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Fact]
    public void Model_marker_drops_application_and_admin_sources()
    {
        var parsed = MemoryProposalCodec.ParseMarkerPayload(
            """
            [{"operation":"upsert","kind":"fact","subject":"rank","content":"hidden","source":"admin"},{"operation":"upsert","kind":"fact","subject":"tone","content":"warm","source":"userExplicit"},{"operation":"upsert","kind":"fact","subject":"channel","content":"app","source":"application"}]
            """);
        var kept = Assert.Single(parsed);
        Assert.Equal("tone", kept.Subject);
        Assert.Equal(MemoryProposalSource.UserExplicit, kept.Source);
    }

    private static MemoryReceipt AssistantReceipt(SessionRuntime runtime) =>
        Assert.Single(runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Envelope!.MemoryReceipts!);

    private static MemoryProposal Proposal(MemoryKind kind, string subject, string content, MemoryProposalSource source) =>
        new(MemoryProposalOperation.Upsert, kind, subject, content, null, source);

    private static async Task<SessionRuntime> RuntimeAsync(
        AgentDefinition definition,
        ILanguageModel model,
        IAgentBrain? brain = null)
    {
        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(Profile(), 0);
        await sessions.SaveAsync(Snapshot(SessionA, definition), 0);
        var runtime = Runtime((await sessions.LoadAsync(SessionA))!, sessions, model, brain);
        await runtime.AttachAsync();
        return runtime;
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore sessions,
        ILanguageModel model,
        IAgentBrain? brain = null) =>
        SessionRuntimeFixture.Create(
            snapshot,            model,            brain ?? new DefaultAgentBrain(new PromptContextBuilder()),            sessions,            new CapturingSessionOutput(),            new DeterministicIdGenerator(
                Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0032-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-0030-7000-8000-0000000000fe")]),            new FakeTimeProvider(Now),            NullLogger<SessionRuntime>.Instance);

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
}
