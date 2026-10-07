using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AgentStepUserTurnAdmissionTests
{
    [Fact]
    public async Task Second_user_turn_succeeds_after_first_turn_no_chat_agent_step_is_rejected()
    {
        var output = new CapturingSessionOutput();
        var turns = new InMemoryConversationTurnExecutionStore();
        var model = new SemanticResponseLanguageModel(new NoChatThenChatLanguageModel());
        await using var runtime = Create(output, model, turns);
        await runtime.AttachAsync();

        await runtime.SubmitUserTextAsync("first");
        await runtime.WaitUntilIdleAsync();

        var firstAssistant = Assert.Single(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, firstAssistant.Status);
        Assert.NotNull(firstAssistant.Failure);
        Assert.Equal("response", firstAssistant.Failure!.Category);
        Assert.Equal("InvalidAgentStep", firstAssistant.Failure.Code);
        Assert.NotEqual(Guid.Empty, firstAssistant.Failure.DiagnosticId);

        var firstUser = runtime.Snapshot.Entries.Single(entry => entry is { Role: ConversationRole.User, Text: "first" });
        var firstSourceEventId = firstUser.SourceEventId ?? firstUser.EntryId;
        var firstExecution = await turns.GetBySourceEventAsync(runtime.SessionId, firstSourceEventId);
        Assert.NotNull(firstExecution);
        Assert.Equal(ConversationTurnExecutionStatus.Failed, firstExecution!.Status);
        Assert.False(firstExecution.IsOpen);

        await runtime.SubmitUserTextAsync("second");
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
        var assistants = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.Assistant).ToArray();
        Assert.Equal(2, assistants.Length);
        var secondAssistant = Assert.Single(assistants, entry => entry.Status == EntryStatus.Completed);
        Assert.Equal("second answer", secondAssistant.Text);
        Assert.NotEqual(firstAssistant.ResponseId, secondAssistant.ResponseId);
        Assert.Empty(await turns.ListOpenForSessionAsync(runtime.SessionId));
        var secondUser = runtime.Snapshot.Entries.Single(entry => entry is { Role: ConversationRole.User, Text: "second" });
        var secondExecution = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            secondUser.SourceEventId ?? secondUser.EntryId);
        Assert.NotNull(secondExecution);
        Assert.Equal(ConversationTurnExecutionStatus.Completed, secondExecution!.Status);
        Assert.NotEqual(firstExecution.ExecutionId, secondExecution.ExecutionId);
        Assert.NotEqual(firstExecution.ResponseId, secondExecution.ResponseId);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput);
        var projected = PublicHistory.FromEntry(firstAssistant);
        Assert.Equal(firstAssistant.Failure!.DiagnosticId, projected.Failure!.DiagnosticId);
        Assert.Equal("InvalidAgentStep", projected.Failure.Code);
        Assert.Contains(
            output.Items,
            item => item.Payload is ResponseCompletedOutput completed
                && !completed.Failed
                && item.ResponseId == secondAssistant.ResponseId);
    }

    [Fact]
    public async Task Later_user_turn_completes_after_non_user_no_chat_return()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var turns = new InMemoryConversationTurnExecutionStore();
        var model = new SemanticResponseLanguageModel(new ChatNoChatChatLanguageModel());
        var definition = SampleDefinitions.Examiner with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                8000,
                5000,
                1,
                ["longSilence"],
                MaxConsecutiveProactiveTurns: 2)
        };
        await using var runtime = Create(
            output,
            model,
            turns,
            new AlwaysSpeakLongSilenceBrain(),
            definition,
            time);
        await runtime.AttachAsync();

        await runtime.SubmitUserTextAsync("hello");
        await runtime.WaitUntilIdleAsync();

        var helloUser = runtime.Snapshot.Entries.Single(entry => entry is { Role: ConversationRole.User, Text: "hello" });
        var helloExecution = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            helloUser.SourceEventId ?? helloUser.EntryId);
        Assert.NotNull(helloExecution);
        Assert.Equal(ConversationTurnExecutionStatus.Completed, helloExecution!.Status);

        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Status == EntryStatus.Interrupted);
        Assert.DoesNotContain(
            output.Items,
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason is not null);

        await runtime.SubmitUserTextAsync("second");
        await runtime.WaitUntilIdleAsync();

        var secondUser = runtime.Snapshot.Entries.Single(entry => entry is { Role: ConversationRole.User, Text: "second" });
        var secondExecution = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            secondUser.SourceEventId ?? secondUser.EntryId);
        Assert.NotNull(secondExecution);
        Assert.Equal(ConversationTurnExecutionStatus.Completed, secondExecution!.Status);
        Assert.Empty(await turns.ListOpenForSessionAsync(runtime.SessionId));

        helloExecution = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            helloUser.SourceEventId ?? helloUser.EntryId);
        Assert.Equal(ConversationTurnExecutionStatus.Completed, helloExecution!.Status);

        var secondAssistant = Assert.Single(
            runtime.Snapshot.Entries,
            entry => entry is { Role: ConversationRole.Assistant, Text: "second answer" });
        Assert.Equal(EntryStatus.Completed, secondAssistant.Status);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        InMemoryConversationTurnExecutionStore turns,
        IAgentBrain? brain = null,
        AgentDefinition? definition = null,
        FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition ?? SampleDefinitions.Examiner,
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
        var store = new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            brain ?? new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turns);
    }

    private sealed class NoChatThenChatLanguageModel : ILanguageModel
    {
        private int _calls;

        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                yield return new ModelSemanticResponseReady(
                    new ModelSemanticResponse(
                        string.Empty,
                        new ModelSpeechProjection(ModelSpeechMode.None, null),
                        [],
                        Disposition: nameof(AgentStepDisposition.Complete),
                        ActionKind: null,
                        ActionSpecified: true));
            }
            else
            {
                yield return new ModelSemanticResponseReady(
                    new ModelSemanticResponse(
                        "second answer",
                        new ModelSpeechProjection(ModelSpeechMode.Same, null),
                        []));
            }

            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class ChatNoChatChatLanguageModel : ILanguageModel
    {
        private int _calls;

        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                yield return new ModelSemanticResponseReady(
                    new ModelSemanticResponse(
                        string.Empty,
                        new ModelSpeechProjection(ModelSpeechMode.None, null),
                        [],
                        Disposition: nameof(AgentStepDisposition.Complete),
                        ActionKind: null,
                        ActionSpecified: true));
            }
            else
            {
                var text = call == 1 ? "hello answer" : "second answer";
                yield return new ModelSemanticResponseReady(
                    new ModelSemanticResponse(
                        text,
                        new ModelSpeechProjection(ModelSpeechMode.Same, null),
                        []));
            }

            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class AlwaysSpeakLongSilenceBrain : IAgentBrain
    {
        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            var builder = new PromptContextBuilder();
            return context.Trigger.Kind switch
            {
                TriggerKind.UserTurn => ValueTask.FromResult<AgentDecision>(
                    new Speak(builder.Build(context, responseId))),
                TriggerKind.LongSilence => ValueTask.FromResult<AgentDecision>(
                    new Speak(builder.Build(context, responseId))),
                _ => ValueTask.FromResult<AgentDecision>(new StaySilent("scripted"))
            };
        }
    }
}
