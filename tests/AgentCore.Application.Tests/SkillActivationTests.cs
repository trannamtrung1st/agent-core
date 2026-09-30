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
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SkillActivationTests
{
    [Fact]
    public void Selector_matches_keywords_in_definition_order_and_stops_at_three()
    {
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"]),
            Skill("billing.note", "BILLING_PROCEDURE", ["billing"]),
            Skill("shipping.note", "SHIPPING_PROCEDURE", ["shipping"]));

        Assert.Equal(
            ["refund.handle"],
            DeterministicSkillSelector.SelectActiveIds(definition, "Please REFUND this"));
        Assert.Equal(
            ["order.lookup"],
            DeterministicSkillSelector.SelectActiveIds(definition, "Check the order"));
        Assert.Equal(
            ["refund.handle", "order.lookup", "billing.note"],
            DeterministicSkillSelector.SelectActiveIds(definition, "refund order billing shipping"));
        Assert.Empty(DeterministicSkillSelector.SelectActiveIds(definition, "hello"));
        Assert.Empty(DeterministicSkillSelector.SelectActiveIds(definition, "   "));
    }

    [Fact]
    public void Prompt_includes_only_pinned_procedures()
    {
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"]));
        var request = new PromptContextBuilder().Build(
            Context(definition, ["refund.handle"]),
            Guid.NewGuid());
        var text = string.Join('\n', request.Messages.Select(message => message.Text));
        Assert.Contains("REFUND_PROCEDURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", text, StringComparison.Ordinal);
        Assert.Equal(
            [ToolCatalog.WorkspaceRead, ToolCatalog.SkillsLoad],
            new PromptContextBuilder().OfferTools(definition, Context(definition, ["refund.handle"])).Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void Pinned_ids_stay_in_the_prompt_when_keywords_would_select_another_skill()
    {
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["other"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"]));
        Assert.Equal(["order.lookup"], DeterministicSkillSelector.SelectActiveIds(definition, "check the order"));
        var request = new PromptContextBuilder().Build(
            Context(definition, ["refund.handle"]),
            Guid.NewGuid());
        var text = string.Join('\n', request.Messages.Select(message => message.Text));
        Assert.Contains("REFUND_PROCEDURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_capabilities_do_not_grant_tools_or_approval()
    {
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"], [ToolCatalog.WebSearch, ToolCatalog.DemoSensitiveAction]));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.WebSearch, ToolConfigurationGates.Unconfigured));
        definition = definition with
        {
            Environment = RoleEnvironment.Empty with
            {
                ToolAllowlist = [ToolCatalog.WebSearch, ToolCatalog.DemoSensitiveAction]
            }
        };
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.WebSearch, ToolConfigurationGates.Unconfigured));
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.DemoSensitiveAction, ToolConfigurationGates.AllowAll));
        Assert.DoesNotContain(
            new PromptContextBuilder().OfferTools(definition, Context(definition, ["refund.handle"])),
            tool => tool.Name == ToolCatalog.WebSearch);
    }

    [Fact]
    public async Task Synthetic_turn_pins_one_skill_and_stores_one_chat_response()
    {
        var model = new RecordingLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"], [ToolCatalog.WorkspaceRead, SkillCapabilities.ChatRespond]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"], [ToolCatalog.WebSearch])));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Please refund this");
        await runtime.WaitUntilIdleAsync();

        var first = Assert.Single(model.Requests);
        var firstText = string.Join('\n', first.Messages.Select(message => message.Text));
        Assert.Contains("REFUND_PROCEDURE", firstText, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", firstText, StringComparison.Ordinal);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Shown", assistant.Text);

        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        var pinned = await turns.GetBySourceEventAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Equal(["refund.handle"], pinned!.PinnedActiveSkillIds);

        await runtime.SubmitUserTextAsync("Check the order");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, model.Requests.Count);
        var secondText = string.Join('\n', model.Requests[1].Messages.Select(message => message.Text));
        Assert.Contains("ORDER_PROCEDURE", secondText, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND_PROCEDURE", secondText, StringComparison.Ordinal);
        var secondUser = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.User);
        var secondPin = await turns.GetBySourceEventAsync(runtime.SessionId, secondUser.SourceEventId ?? secondUser.EntryId);
        Assert.Equal(["order.lookup"], secondPin!.PinnedActiveSkillIds);
        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed));
    }

    [Fact]
    public async Task Queued_batch_pins_the_later_user_text_on_the_shared_response()
    {
        var model = new GatedRecordingLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition(
            Skill("greeting.note", "GREETING_PROCEDURE", ["status"]),
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"])));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await model.FirstRequestStarted.Task;

        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "status please",
            Guid.Parse("019944af-0000-7000-8000-0000000000b1"),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "please refund this",
            Guid.Parse("019944af-0000-7000-8000-0000000000b2"),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var batch = model.Requests.Single(request =>
            request.Messages.Any(message => message.Text.Contains("please refund this", StringComparison.Ordinal)));
        var batchText = string.Join('\n', batch.Messages.Select(message => message.Text));
        Assert.Contains("REFUND_PROCEDURE", batchText, StringComparison.Ordinal);
        Assert.DoesNotContain("GREETING_PROCEDURE", batchText, StringComparison.Ordinal);

        var earlier = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            Guid.Parse("019944af-0000-7000-8000-0000000000b1"));
        var later = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            Guid.Parse("019944af-0000-7000-8000-0000000000b2"));
        Assert.Equal(later!.ResponseId, earlier!.ResponseId);
        Assert.Equal(["refund.handle"], earlier.PinnedActiveSkillIds);
        var reloaded = await turns.GetAsync(earlier.ExecutionId);
        Assert.Equal(["refund.handle"], reloaded!.PinnedActiveSkillIds);
    }

    [Fact]
    public async Task Deferred_batch_pins_the_later_user_text_before_the_model_request()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new RecordingScriptedModel(new ScriptedLanguageModel(
            compactionFixture: CompactionFixture.Late,
            compactionRelease: release,
            compactionStarted: started));
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var definition = Definition(
            Skill("greeting.note", "GREETING_PROCEDURE", ["status"]),
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]));
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            DeferredHistory(now),
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
        await memory.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turns);
        runtime.TestDeferredUserTurnEstablished = deferred;

        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "continue",
            Guid.Parse("019944af-0008-7000-8000-0000000000d1")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "status please",
            Guid.Parse("019944af-0008-7000-8000-0000000000d2")));
        await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requestsBeforeReplacement = model.ConversationRequests.Count;
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "please refund this",
            Guid.Parse("019944af-0008-7000-8000-0000000000d3")));
        Assert.Equal(requestsBeforeReplacement, model.ConversationRequests.Count);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var batch = Assert.Single(
            model.ConversationRequests,
            request => request.Messages.Any(message =>
                message.Text.Contains("please refund this", StringComparison.Ordinal)));
        var batchText = string.Join('\n', batch.Messages.Select(message => message.Text));
        Assert.Contains("REFUND_PROCEDURE", batchText, StringComparison.Ordinal);
        Assert.DoesNotContain("GREETING_PROCEDURE", batchText, StringComparison.Ordinal);
        var earlier = await turns.GetBySourceEventAsync(
            runtime.SessionId,
            Guid.Parse("019944af-0008-7000-8000-0000000000d2"));
        Assert.Equal(["refund.handle"], earlier!.PinnedActiveSkillIds);
        var reloaded = await turns.GetAsync(earlier.ExecutionId);
        Assert.Equal(["refund.handle"], reloaded!.PinnedActiveSkillIds);
    }

    private static IReadOnlyList<ConversationEntry> DeferredHistory(DateTimeOffset now)
    {
        var entries = new List<ConversationEntry>(40);
        for (var sequence = 1; sequence <= 40; sequence++)
        {
            var role = sequence % 2 == 0 ? ConversationRole.Assistant : ConversationRole.User;
            var text = $"turn-{sequence}";
            entries.Add(new ConversationEntry(
                Guid.Parse($"019944af-0006-7000-8000-{sequence:D12}"),
                sequence,
                null,
                role,
                text,
                null,
                EntryStatus.Completed,
                SessionMode.Text,
                text.Length,
                text.Length,
                now.AddSeconds(sequence)));
        }

        return entries;
    }

    private static AgentContext Context(AgentDefinition definition, IReadOnlyList<string> activeIds) =>
        new(
            definition,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "hello"),
            ActiveSkillIds: activeIds);

    private static AgentDefinition Definition(params SkillSpec[] skills) =>
        SampleDefinitions.Examiner with
        {
            Voice = new VoiceConfiguration(false, "default", 1),
            ProviderPreferences = new ProviderPreferences("primary-llm", null, null),
            Environment = RoleEnvironment.Empty with { ToolAllowlist = [ToolCatalog.WorkspaceRead] },
            Skills = skills
        };

    private static SkillSpec Skill(
        string id,
        string procedure,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string>? capabilities = null) =>
        new(id, id, "", procedure, keywords, capabilities ?? [], []);

    private static SessionRuntime Create(
        ILanguageModel model,
        InMemoryConversationTurnExecutionStore turns,
        AgentDefinition definition)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
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
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turns);
    }

    private sealed class RecordingLanguageModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class GatedRecordingLanguageModel : ILanguageModel
    {
        private int _calls;

        public List<ModelRequest> Requests { get; } = [];

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Requests.Add(request);
            if (call == 1)
            {
                FirstRequestStarted.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class RecordingScriptedModel(ScriptedLanguageModel inner) : ILanguageModel
    {
        public List<ModelRequest> ConversationRequests { get; } = [];

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!request.Messages.Any(message =>
                    message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal)))
            {
                ConversationRequests.Add(request);
            }

            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
