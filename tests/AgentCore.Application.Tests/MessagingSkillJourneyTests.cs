using AgentCore.Application.Context;
using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed partial class MessagingSkillJourneyTests
{
    [Theory]
    [InlineData("retired")]
    [InlineData("disabled")]
    public async Task Unavailable_skill_only_input_is_rejected_without_a_user_entry_or_model_call(string id)
    {
        var recording = new RecordingModel(new ScriptedLanguageModel());
        var definition = Definition(Skill("disabled", "DISABLED_PROCEDURE") with { DefaultEnabled = false });
        await using var runtime = Create(new SemanticResponseLanguageModel(recording), new(), new(), new(), definition);
        await runtime.AttachAsync();
        UserMessagePart[] parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:" + id)];
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.SubmitPersistedUserTextAsync(UserMessageContent.DisplayText(parts), Guid.NewGuid(), parts: parts));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(recording.Requests);
        Assert.DoesNotContain(runtime.Snapshot.Entries, e => e.Role == ConversationRole.User);
        Assert.Equal(SessionTitles.Default, runtime.Snapshot.Title);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Skill_only_turn_is_accepted_answers_and_preserves_its_run_and_title(int count, bool renamed)
    {
        var recording = new RecordingModel(new ScriptedLanguageModel());
        var turns = new RuntimeAgentRunStore(); var memory = new InMemoryMemoryStore();
        var definition = Definition(Skill("say", "SAY_PROCEDURE"), Skill("research", "RESEARCH_PROCEDURE"));
        await using var seed = Create(new SemanticResponseLanguageModel(recording), new(), new(), new(), definition);
        var initial = seed.Snapshot with { Title = renamed ? "My title" : SessionTitles.Default };
        await using var runtime = Create(new SemanticResponseLanguageModel(recording), new(), turns, memory, definition, initial);
        await runtime.AttachAsync();
        var parts = new[] { new UserMessagePart("invocation", InvocationKind: "skill", SkillKey: "definition:say"),
            new UserMessagePart("invocation", InvocationKind: "skill", SkillKey: "definition:research") }.Take(count).ToArray();
        Assert.True(await runtime.SubmitUserTextAsync(UserMessageContent.DisplayText(parts), parts: parts));
        await runtime.WaitUntilIdleAsync();
        var user = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.User);
        Assert.Equal(UserMessageContent.DisplayText(parts), user.Text);
        Assert.Equal(parts, user.Parts);
        Assert.Contains(runtime.Snapshot.Entries, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
        Assert.Equal(renamed ? "My title" : count == 1 ? "/say" : "/say + /research", runtime.Snapshot.Title);
        var first = recording.Requests[0];
        Assert.Contains(first.Messages, m => m.Role == ModelRole.User && m.Text.Contains("Explicitly invoked Skill:") && m.Text.Contains("key=definition:say"));
        Assert.Contains("SAY_PROCEDURE", Text(first));
        if (count == 2) Assert.Contains("RESEARCH_PROCEDURE", Text(first));
        var run = (await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId!.Value))!;
        Assert.Equal(UserMessageContent.ExplicitSkills(parts), run.Admission.ComposerInput!.ExplicitSkillKeys);
        Assert.Equal(run.Admission.ComposerInput.ExplicitSkillKeys, run.ActiveSkillKeys);
        Assert.Equal(0, run.SkillLoadCount);
        var saved = (await memory.LoadAsync(runtime.SessionId))!;
        await using var reopened = Create(new SemanticResponseLanguageModel(recording), new(), turns, memory, definition, saved, idStart: 101);
        await reopened.AttachAsync(); await reopened.WaitUntilIdleAsync();
        var before = recording.Requests.Count;
        Assert.True(await reopened.SubmitUserTextAsync("Continue normally"));
        await reopened.WaitUntilIdleAsync();
        var next = recording.Requests[before];
        Assert.Contains(next.Messages, m => m.Role == ModelRole.User && m.Text.Contains("Previously invoked Skill (historical, not activated):"));
        Assert.DoesNotContain("SAY_PROCEDURE", Text(next));
        Assert.DoesNotContain("RESEARCH_PROCEDURE", Text(next));
        Assert.Equal(run.ActiveSkillKeys, (await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId.Value))!.ActiveSkillKeys);
        Assert.Equal(saved.Title, reopened.Snapshot.Title);
    }

    // Actual owned source/message/Skill activation and SQLite host-recreation parity
    // is in TenantSessionAccessTests.MessagingSkillJourney; pure composer cases stay here.

    [Fact]
    public async Task Explicit_skills_are_active_in_the_first_request_without_load_and_do_not_leak_to_next_turn()
    {
        var recording = new RecordingModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput(); var turns = new RuntimeAgentRunStore(); var memory = new InMemoryMemoryStore();
        var definition = Definition(Skill("first", "EXPLICIT_FIRST_PROCEDURE"), Skill("second", "EXPLICIT_SECOND_PROCEDURE"));
        await using var runtime = Create(new SemanticResponseLanguageModel(recording), output, turns, memory, definition);
        await runtime.AttachAsync();
        UserMessagePart[] parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:first"),
            new("text", Text: " Review this using "), new("invocation", InvocationKind: "skill", SkillKey: "definition:second")];
        await runtime.SubmitUserTextAsync(UserMessageContent.DisplayText(parts), parts: parts);
        await runtime.WaitUntilIdleAsync();
        Assert.NotEmpty(recording.Requests);
        Assert.Contains("EXPLICIT_FIRST_PROCEDURE", Text(recording.Requests[0]));
        Assert.Contains("EXPLICIT_SECOND_PROCEDURE", Text(recording.Requests[0]));
        var user = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.User);
        var run = await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Equal(["definition:first", "definition:second"], run!.ActiveSkillKeys);
        Assert.Equal(0, run.SkillLoadCount);
        Assert.Equal(2, run.Admission.ComposerInput!.ExplicitSkillKeys.Count);
        var before = recording.Requests.Count;
        await runtime.SubmitUserTextAsync("A new plain turn"); await runtime.WaitUntilIdleAsync();
        Assert.True(recording.Requests.Count > before);
        Assert.DoesNotContain("EXPLICIT_FIRST_PROCEDURE", Text(recording.Requests[before]));
        Assert.DoesNotContain("EXPLICIT_SECOND_PROCEDURE", Text(recording.Requests[before]));
    }

    [Fact]
    public async Task Accepted_explicit_input_retired_before_recovery_fails_without_a_model_call()
    {
        var recording = new RecordingModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput(); var turns = new RuntimeAgentRunStore(); var memory = new InMemoryMemoryStore();
        var definition = Definition();
        await using var seed = Create(new SemanticResponseLanguageModel(recording), output, turns, memory, definition);
        UserMessagePart[] parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:retired"), new("text", Text: " Review this")];
        var entry = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User,
            UserMessageContent.DisplayText(parts), null, EntryStatus.Completed, SessionMode.Text, 0, 0, DateTimeOffset.UtcNow, Parts: parts);
        var accepted = seed.Snapshot with { Entries = [entry], LastEntrySequence = 1, PendingAgentInputIds = [entry.EntryId], Revision = seed.Snapshot.Revision + 1 };
        await memory.SaveAsync(accepted, accepted.Revision - 1);
        var saved = (await memory.LoadAsync(seed.SessionId))!;
        await using var recovered = Create(new SemanticResponseLanguageModel(recording), output, turns, memory, definition, saved);
        await recovered.AttachAsync(); await recovered.WaitUntilIdleAsync();
        Assert.Empty(recording.Requests);
        Assert.Equal(parts, Assert.Single(recovered.Snapshot.Entries, e => e.Role == ConversationRole.User).Parts);
        var run = await turns.ForSourceAsync(recovered.SessionId, entry.SourceEventId!.Value);
        Assert.NotNull(run!.Admission.ComposerInput!.Error);
        Assert.Contains("definition:retired", run.Admission.ComposerInput.ExplicitSkillKeys);
        Assert.Contains(output.Items, e => e.Payload is ErrorOutput { Code: "ComposerInputUnavailable", Fatal: false });
    }

    private static string Text(ModelRequest request) =>
        string.Join('\n', request.Messages.Select(message => message.Text));

    private static AgentDefinition Definition(params SkillSpec[] skills) =>
        SampleDefinitions.Examiner with
        {
            Voice = new VoiceConfiguration(true, "default", 1),
            ProviderPreferences = new ProviderPreferences(null, null),
            Skills = skills,
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceRead])
        };

    private static SkillSpec Skill(string id, string procedure) =>
        new(id, id, "Procedure", procedure, SkillProjection.OnDemand, true, [], []);

    private static SessionRuntime Create(
        ILanguageModel model,
        CapturingSessionOutput output,
        RuntimeAgentRunStore turns,
        InMemoryMemoryStore memory,
        AgentDefinition definition,
        SessionSnapshot? snapshot = null,
        int idStart = 1)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(idStart, 80).Select(index => Guid.Parse($"019944af-00f5-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "dca20000-0000-4000-8000-000000000001",
                "scripted",
                ModelSelectionSource.TenantDefault,
                null), AgentInstanceId: Guid.NewGuid());
        snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        turns.Bind(memory);
        if (snapshot.Entries.Count == 0)
        {
            memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        var instances = new InMemoryAgentInstanceStore();
        instances.InsertAsync(new(snapshot.AgentInstanceId, definition.Id, definition.Version, definition.Identity,
            AgentInstanceLifecycle.Active, now, now), initialSkills: definition.SkillList).AsTask().GetAwaiter().GetResult();
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            agentRuns: turns, tools: new SessionToolExecutor());
    }

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
