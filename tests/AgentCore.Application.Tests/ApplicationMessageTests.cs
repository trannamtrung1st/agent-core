using System.Text.Json;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ApplicationMessageTests
{
    private const string FirstText = "Still checking the order";
    private const string SecondText = "Second notice";
    private const string ThirdText = "Third notice";
    private const string FourthText = "Fourth notice";
    private const string ChangedText = "Changed text";
    private const string HallucinatedBatchText = "I'm working on it";

    [Fact]
    public void Payload_rules_reject_routing_empty_and_oversize_text()
    {
        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"  "}""").RootElement,
            out _,
            out var empty));
        Assert.Contains("empty", empty, StringComparison.OrdinalIgnoreCase);

        var oversize = new string('x', ApplicationMessageLimits.MaxCharacters + 1);
        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse(JsonSerializer.Serialize(new { text = oversize })).RootElement,
            out _,
            out var tooLong));
        Assert.Contains("too long", tooLong, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oversize, tooLong, StringComparison.Ordinal);

        Assert.False(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"hello","sessionId":"secret"}""").RootElement,
            out _,
            out var routed));
        Assert.Contains("only text", routed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", routed, StringComparison.Ordinal);

        Assert.True(ApplicationMessageAdmission.TryParseText(
            JsonDocument.Parse("""{"text":"  hello  "}""").RootElement,
            out var text,
            out _));
        Assert.Equal("hello", text);

        var executionId = Guid.Parse("019944af-00ee-7000-8000-0000000000e1");
        Assert.False(ApplicationMessageAdmission.TryCreateEffectKey(executionId, new string('k', 170), out _));
        Assert.True(ApplicationMessageAdmission.TryCreateEffectKey(executionId, "m1", out var key));
        Assert.Equal($"v1:{executionId:N}:m1", key);
        Assert.True(key.Length <= ApplicationMessageLimits.MaxEffectKeyCharacters);
    }

    [Fact]
    public void Empty_skill_list_does_not_offer_application_messages_before_substantive_work()
    {
        var definition = Definition();
        Assert.Empty(definition.SkillList);
        var context = new AgentContext(
            definition,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hello"));
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll).Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(ToolCatalog.AppMessageSend, offered);
        Assert.DoesNotContain(ToolCatalog.SkillsLoad, offered);
        Assert.Contains(ToolCatalog.WorkspaceRead, offered);
        var unlocked = context with { IntermediateMessagingAllowed = true };
        Assert.Contains(
            ToolCatalog.AppMessageSend,
            ToolCatalog.For(definition, unlocked, ToolConfigurationGates.AllowAll).Select(tool => tool.Name));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn, IntermediateMessagingAllowed: true)));
    }

    [Fact]
    public async Task Direct_tool_execution_does_not_append_an_application_message()
    {
        var executor = new SessionToolExecutor();
        var result = await executor.ExecuteAsync(
            Definition(),
            Guid.NewGuid(),
            new ModelToolCall("m1", ToolCatalog.AppMessageSend, """{"text":"Still checking the order"}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        Assert.Contains("owned by the session runtime", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstText, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_compaction_and_completion_omit_application_message_text()
    {
        var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000aa");
        var message = ApplicationEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000ab"), 2, responseId, FirstText, now);
        var definition = Definition();
        var history = new List<ConversationEntry>
        {
            PromptEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000a1"), 1, ConversationRole.User, "hello", now),
            message,
            PromptEntry(Guid.Parse("019944af-00ee-7000-8000-0000000000a2"), 3, ConversationRole.Assistant, "Shown", now)
        };
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                definition,
                history,
                string.Empty,
                null,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "thanks"),
                ActiveSkillIds: []),
            Guid.NewGuid());
        var prompt = string.Join('\n', request.Messages.Select(item => item.Text));
        Assert.Contains("hello", prompt, StringComparison.Ordinal);
        Assert.Contains("Shown", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstText, prompt, StringComparison.Ordinal);

        var page = new List<ConversationEntry> { message };
        for (var index = 0; index < CompactionPolicy.TriggerEligibleEntries; index++)
        {
            var role = index % 2 == 0 ? ConversationRole.User : ConversationRole.Assistant;
            page.Add(PromptEntry(
                Guid.Parse($"019944af-00ef-7000-8000-{index + 1:D12}"),
                index + 10,
                role,
                $"turn-{index}",
                now));
        }

        var selected = CompactionSourceSelector.Select(page, string.Empty, 0);
        Assert.True(selected.Ready);
        Assert.DoesNotContain(message.EntryId, selected.Source.Select(entry => entry.EntryId));
        Assert.DoesNotContain(FirstText, selected.PromptText, StringComparison.Ordinal);
        Assert.Contains("turn-0", selected.PromptText, StringComparison.Ordinal);

        var snapshot = new SessionSnapshot(
            1,
            Guid.Parse("019944af-00ee-7000-8000-0000000000c1"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            history,
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        var evaluation = string.Join('\n', CompletionEvaluator.CreateEvaluationRequest(snapshot, now).Messages.Select(item => item.Text));
        Assert.DoesNotContain(FirstText, evaluation, StringComparison.Ordinal);
        Assert.Contains("Shown", evaluation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_generation_batch_denies_app_message_until_the_next_generation()
    {
        var model = new BatchedHallucinatedMessageLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition());
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.ApplicationMessage
                && entry.Text == HallucinatedBatchText);
        Assert.Contains(ToolCatalog.AppMessageSend, model.Requests[1].Tools!.Select(tool => tool.Name));
        Assert.Contains(FirstText, runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.ApplicationMessage).Text);
    }

    [Fact]
    public async Task Casual_greeting_completes_with_chat_respond_and_no_application_messages()
    {
        var model = new GreetingLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition());
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("how you doing");
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.ApplicationMessage);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Doing well, thanks for asking.", assistant.Text);
        Assert.Null(assistant.Failure);
        var initialTools = model.Requests[0].Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.DoesNotContain(ToolCatalog.AppMessageSend, initialTools);
    }

    [Fact]
    public async Task Send_admits_three_visible_messages_and_the_next_turn_does_not_see_them()
    {
        var model = new MessagingLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(model, turns, Definition(), output);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(9, model.Requests.Count);
        Assert.DoesNotContain(ToolCatalog.AppMessageSend, model.Requests[0].Tools!.Select(tool => tool.Name));
        Assert.DoesNotContain(ToolCatalog.SkillsLoad, model.Requests[0].Tools!.Select(tool => tool.Name));
        Assert.Contains(ToolCatalog.WorkspaceList, model.Requests[0].Tools!.Select(tool => tool.Name));
        Assert.Contains(ToolCatalog.AppMessageSend, model.Requests[1].Tools!.Select(tool => tool.Name));
        Assert.Contains("duplicate", Text(model.Requests[3]), StringComparison.Ordinal);
        Assert.Contains("only text", Text(model.Requests[4]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit reached", Text(model.Requests[8]), StringComparison.OrdinalIgnoreCase);
        foreach (var request in model.Requests)
        {
            var text = Text(request);
            Assert.DoesNotContain(FirstText, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecondText, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ThirdText, text, StringComparison.Ordinal);
            Assert.DoesNotContain(FourthText, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ChangedText, text, StringComparison.Ordinal);
        }

        var admitted = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.ApplicationMessage).ToArray();
        Assert.Equal([FirstText, SecondText, ThirdText], admitted.Select(entry => entry.Text).ToArray());
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry =>
            entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        Assert.Equal("Shown", assistant.Text);
        foreach (var entry in admitted)
        {
            Assert.True(entry.Sequence > assistant.Sequence);
            Assert.Equal(assistant.ResponseId, entry.ResponseId);
            Assert.Equal(EntryStatus.Completed, entry.Status);
            Assert.Equal(0, entry.HeardTextEndExclusive);
            Assert.Equal(entry.Text.Length, entry.ReceivedTextEndExclusive);
            Assert.Null(entry.Envelope);
            Assert.Null(entry.Failure);
            Assert.Null(entry.SourceAdmissionFingerprint);
            Assert.StartsWith("v1:", entry.ApplicationMessageEffectKey, StringComparison.Ordinal);
            Assert.True(entry.ApplicationMessageEffectKey!.Length <= ApplicationMessageLimits.MaxEffectKeyCharacters);
        }

        Assert.Equal(3, admitted.Select(entry => entry.ApplicationMessageEffectKey).Distinct().Count());
        var published = output.Items.Where(item => item.Payload is HistoryEntryUpsertOutput).ToArray();
        var upserts = published.Select(item => (HistoryEntryUpsertOutput)item.Payload).ToArray();
        Assert.Equal([FirstText, SecondText, ThirdText], upserts.Select(item => item.Entry.Text).ToArray());
        Assert.Equal(upserts.Select(item => item.Entry.ResponseId).ToArray(), published.Select(item => item.ResponseId).ToArray());
        Assert.All(upserts, item =>
        {
            Assert.Equal(ConversationRole.ApplicationMessage, item.Entry.Role);
            Assert.Null(item.Entry.SpeechText);
            Assert.Equal(0, item.Entry.HeardTextEndExclusive);
        });
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechOutputSegmentOutput or SpeechOutputCompletedOutput);
        await runtime.SubmitUserTextAsync("thanks");
        await runtime.WaitUntilIdleAsync();
        var next = Text(model.Requests[^1]);
        Assert.DoesNotContain(FirstText, next, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondText, next, StringComparison.Ordinal);
        Assert.DoesNotContain(ThirdText, next, StringComparison.Ordinal);
        Assert.Contains("thanks", next, StringComparison.Ordinal);
        Assert.Contains("hello", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_send_writes_nothing_and_keeps_an_already_admitted_message()
    {
        var model = new CancelMessagingLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(model, turns, Definition(), output);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(runtime.ActiveResponseId);
        await runtime.CancelResponseAsync(runtime.ActiveResponseId!.Value);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var admitted = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.ApplicationMessage);
        Assert.Equal(FirstText, admitted.Text);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains(SecondText, StringComparison.Ordinal));
        var upsert = Assert.Single(output.Items.Select(item => item.Payload).OfType<HistoryEntryUpsertOutput>());
        Assert.Equal(FirstText, upsert.Entry.Text);
        Assert.Equal(admitted.EntryId, upsert.Entry.EntryId);
    }

    [Fact]
    public async Task Steered_turn_drops_the_late_application_message_and_does_not_speak_it()
    {
        var model = new LateMessageLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(model, turns, Definition(), output);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var superseded = runtime.ActiveResponseId;
        Assert.NotNull(superseded);

        await runtime.SubmitPersistedUserTextAsync(
            "take over",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Interrupt);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains(SecondText, StringComparison.Ordinal));
        Assert.DoesNotContain(
            output.Items.Select(item => item.Payload).OfType<HistoryEntryUpsertOutput>(),
            item => item.Entry.Text.Contains(SecondText, StringComparison.Ordinal));
        var assistant = Assert.Single(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        Assert.Equal("Noted", assistant.Text);
        Assert.NotEqual(superseded, assistant.ResponseId);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User && entry.Text == "take over");
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechOutputSegmentOutput or SpeechOutputCompletedOutput);
    }

    private static string Text(ModelRequest request) =>
        string.Join('\n', request.Messages.Select(message => message.Text));

    private static ConversationEntry PromptEntry(Guid id, long sequence, ConversationRole role, string text, DateTimeOffset now) =>
        new(id, sequence, id, role, text, role == ConversationRole.Assistant ? id : null, EntryStatus.Completed, SessionMode.Text, text.Length, text.Length, now);

    private static ConversationEntry ApplicationEntry(Guid id, long sequence, Guid responseId, string text, DateTimeOffset now) =>
        new(
            id,
            sequence,
            null,
            ConversationRole.ApplicationMessage,
            text,
            responseId,
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            text.Length,
            now,
            ApplicationMessageEffectKey: $"v1:{responseId:N}:unit");

    private static AgentDefinition Definition() =>
        SampleDefinitions.Examiner with
        {
            Voice = new VoiceConfiguration(false, "default", 1),
            ProviderPreferences = new ProviderPreferences("primary-llm", null, null),
            Skills = [],
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceRead])
        };

    private static SessionRuntime Create(
        ILanguageModel model,
        InMemoryConversationTurnExecutionStore turns,
        AgentDefinition definition,
        CapturingSessionOutput? output = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00ee-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
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
                null));
        var memory = new InMemoryMemoryStore();
        memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output ?? new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turns);
    }

    private sealed class BatchedHallucinatedMessageLanguageModel : ILanguageModel
    {
        private int _generation;

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var generation = Interlocked.Increment(ref _generation);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            switch (generation)
            {
                case 1:
                    yield return new ModelToolCallEvent(new ModelToolCall("wk-1", ToolCatalog.WorkspaceList, "{}"));
                    yield return new ModelToolCallEvent(new ModelToolCall(
                        "m-hallucinated",
                        ToolCatalog.AppMessageSend,
                        JsonSerializer.Serialize(new { text = HallucinatedBatchText })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 2:
                    yield return Call("m1", JsonSerializer.Serialize(new { text = FirstText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                default:
                    yield return new ModelSemanticResponseReady(
                        new ModelSemanticResponse(
                            "Done",
                            new ModelSpeechProjection(ModelSpeechMode.Same, null),
                            []));
                    yield return new ModelCompleted(ModelStopReason.Completed);
                    yield break;
            }
        }

        private static ModelToolCallEvent Call(string id, string arguments) =>
            new(new ModelToolCall(id, ToolCatalog.AppMessageSend, arguments));
    }

    private sealed class GreetingLanguageModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse(
                    "Doing well, thanks for asking.",
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class MessagingLanguageModel : ILanguageModel
    {
        private int _generation;

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var generation = Interlocked.Increment(ref _generation);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            switch (generation)
            {
                case 1:
                    yield return new ModelToolCallEvent(new ModelToolCall("wk-1", ToolCatalog.WorkspaceList, "{}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 2:
                    yield return Call("m1", JsonSerializer.Serialize(new { text = FirstText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 3:
                    yield return Call("m1", JsonSerializer.Serialize(new { text = ChangedText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 4:
                    yield return Call("m-route", """{"text":"Nope","sessionId":"secret-session"}""");
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 5:
                    yield return Call("m-dup", JsonSerializer.Serialize(new { text = $"  {FirstText}  " }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 6:
                    yield return Call("m2", JsonSerializer.Serialize(new { text = SecondText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 7:
                    yield return Call("m3", JsonSerializer.Serialize(new { text = ThirdText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                case 8:
                    yield return Call("m4", JsonSerializer.Serialize(new { text = FourthText }));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                default:
                    yield return new ModelSemanticResponseReady(
                        new ModelSemanticResponse(
                            generation == 9 ? "Shown" : "Noted",
                            new ModelSpeechProjection(ModelSpeechMode.Same, null),
                            []));
                    yield return new ModelCompleted(ModelStopReason.Completed);
                    yield break;
            }
        }

        private static ModelToolCallEvent Call(string id, string arguments) =>
            new(new ModelToolCall(id, ToolCatalog.AppMessageSend, arguments));
    }

    private sealed class CancelMessagingLanguageModel : ILanguageModel
    {
        private int _generation;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var generation = Interlocked.Increment(ref _generation);
            await Task.Yield();
            if (generation == 1)
            {
                yield return new ModelToolCallEvent(new ModelToolCall("wk-1", ToolCatalog.WorkspaceList, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            if (generation == 2)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "m1",
                    ToolCatalog.AppMessageSend,
                    JsonSerializer.Serialize(new { text = FirstText })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelToolCallEvent(new ModelToolCall(
                "m2",
                ToolCatalog.AppMessageSend,
                JsonSerializer.Serialize(new { text = SecondText })));
            Entered.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class LateMessageLanguageModel : ILanguageModel
    {
        private int _generation;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var generation = Interlocked.Increment(ref _generation);
            await Task.Yield();
            if (generation == 1)
            {
                yield return new ModelToolCallEvent(new ModelToolCall("wk-1", ToolCatalog.WorkspaceList, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            if (generation == 2)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "m1",
                    ToolCatalog.AppMessageSend,
                    JsonSerializer.Serialize(new { text = FirstText })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            if (generation == 3)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "m2",
                    ToolCatalog.AppMessageSend,
                    JsonSerializer.Serialize(new { text = SecondText })));
                Entered.TrySetResult();
                await Release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse(
                    "Noted",
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
