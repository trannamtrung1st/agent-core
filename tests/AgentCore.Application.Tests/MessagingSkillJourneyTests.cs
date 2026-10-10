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

public sealed class MessagingSkillJourneyTests
{
    [Fact]
    public async Task Scripted_journey_sends_one_message_loads_the_missed_skill_and_answers_once()
    {
        var userText = $"{ScriptedLanguageModel.MessagingSkillJourneyMarker} please review this account";
        var definition = Definition(
            Skill(
                ScriptedLanguageModel.MessagingSkillJourneyTargetSkill,
                ScriptedLanguageModel.MessagingSkillJourneyProcedure),
            Skill("order.lookup", "ORDER_PROCEDURE"));

        var recording = new RecordingModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput();
        var turns = new RuntimeAgentRunStore();
        var memory = new InMemoryMemoryStore();
        await using var runtime = Create(new SemanticResponseLanguageModel(recording), output, turns, memory, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync(userText);
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(4, recording.Requests.Count);
        var first = Text(recording.Requests[0]);
        var afterMessage = Text(recording.Requests[2]);
        var afterLoad = Text(recording.Requests[3]);
        Assert.DoesNotContain(ScriptedLanguageModel.MessagingSkillJourneyProcedure, first, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", first, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedLanguageModel.MessagingSkillJourneyProcedure, afterMessage, StringComparison.Ordinal);
        Assert.Contains(ScriptedLanguageModel.MessagingSkillJourneyProcedure, afterLoad, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", afterLoad, StringComparison.Ordinal);
        Assert.Contains("key: definition:order.lookup", first, StringComparison.Ordinal);

        var application = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.ApplicationMessage);
        Assert.Equal(ScriptedLanguageModel.MessagingSkillJourneyMessage, application.Text);
        Assert.Equal(0, application.HeardTextEndExclusive);
        Assert.Null(application.Envelope);
        var assistant = Assert.Single(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        Assert.Equal(ScriptedLanguageModel.MessagingSkillJourneyAnswer, assistant.Text);
        Assert.Equal(assistant.ResponseId, application.ResponseId);
        Assert.True(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length));
        application = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.ApplicationMessage);
        Assert.Null(application.Envelope);
        Assert.Equal(application.Text.Length, application.ReceivedTextEndExclusive);
        Assert.Equal(0, application.HeardTextEndExclusive);
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechOutputSegmentOutput or SpeechOutputCompletedOutput);

        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        var pinned = await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Equal(["definition:" + ScriptedLanguageModel.MessagingSkillJourneyTargetSkill], pinned!.ActiveSkillKeys);
        Assert.Equal(1, pinned.SkillLoadCount);

        var saved = await memory.LoadAsync(runtime.SessionId);
        Assert.NotNull(saved);
        var reopenedRecording = new RecordingModel(new ScriptedLanguageModel());
        await using var reopened = Create(
            new SemanticResponseLanguageModel(reopenedRecording),
            new CapturingSessionOutput(),
            turns,
            memory,
            definition,
            saved);
        await reopened.AttachAsync();
        await reopened.WaitUntilIdleAsync();

        Assert.Empty(reopenedRecording.Requests);
        Assert.Equal(saved.Entries.Count, reopened.Snapshot.Entries.Count);
        Assert.Single(reopened.Snapshot.Entries, entry => entry.Role == ConversationRole.ApplicationMessage);
        Assert.Single(
            reopened.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        Assert.Equal(
            ["definition:" + ScriptedLanguageModel.MessagingSkillJourneyTargetSkill],
            (await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId))!.ActiveSkillKeys);
    }

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
            ProviderPreferences = new ProviderPreferences("primary-llm", null, null),
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
        SessionSnapshot? snapshot = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00f5-7000-8000-{index:D12}")),
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
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
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
            agentRuns: turns, tools: new SessionToolExecutor(agentInstances: instances));
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
