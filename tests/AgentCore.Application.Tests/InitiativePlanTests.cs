using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class InitiativePlanTests
{
    private const string VietnamQuestion = "What do you enjoy most about living in Vietnam?";
    private const string HintObjective =
        "Candidate hesitated after the Vietnam enjoyment question; offer angles without repeating the question.";

    [Fact]
    public async Task Proactive_hint_plan_reaches_generation_without_repeating_question()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var capture = new CapturingGenerationModel(
            initiativeJson:
            [
                $$"""{"decision":"speak","intent":"hint","objective":"{{HintObjective}}"}"""
            ],
            generationChunks:
            [
                VietnamQuestion,
                "I hear you.",
                "You could think about the food, the people, or the convenience of daily life. Pick one and explain why you like it."
            ]);
        await using var runtime = CreateExaminerRuntime(output, capture, time);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Ready.");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitUserTextAsync("hmmm");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();

        Assert.Contains("Proactive initiative intent: hint", capture.LastGenerationRequest, StringComparison.Ordinal);
        Assert.Contains("initiative_planner_observation", capture.LastGenerationRequest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HintObjective, capture.LastGenerationRequest, StringComparison.Ordinal);
        Assert.Contains("food, the people", output.Items
            .Select(item => item.Payload)
            .OfType<TextDeltaOutput>()
            .Select(delta => delta.Text)
            .Aggregate(string.Empty, (left, right) => left + right), StringComparison.Ordinal);
        Assert.DoesNotContain(
            output.Items
                .Select(item => item.Payload)
                .OfType<TextDeltaOutput>()
                .Select(delta => delta.Text)
                .Aggregate(string.Empty, (left, right) => left + right),
            VietnamQuestion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Examiner_identity_instructs_direct_hints_when_candidate_asks()
    {
        var identity = PromptContextBuilder.BuildIdentitySystem(SampleDefinitions.Examiner);
        Assert.Contains("explicitly asks for help or a hint", identity, StringComparison.Ordinal);
        Assert.Contains("give the hint directly", identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_turn_prompt_includes_direct_hint_instruction_for_explicit_requests()
    {
        var builder = new PromptContextBuilder();
        var now = DateTimeOffset.UtcNow;
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    "Can I get a hint?",
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    18,
                    18,
                    now.AddMinutes(-1)),
                new ConversationEntry(
                    Guid.NewGuid(),
                    2,
                    Guid.NewGuid(),
                    ConversationRole.Assistant,
                    VietnamQuestion,
                    Guid.NewGuid(),
                    EntryStatus.Completed,
                    SessionMode.Text,
                    VietnamQuestion.Length,
                    VietnamQuestion.Length,
                    now)
            ],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, null),
            UtcNow: now);
        var request = builder.Build(context, Guid.NewGuid());
        var identity = request.Messages.First(message => message.Role == ModelRole.System).Text;
        Assert.Contains("give the hint directly", identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_silence_after_hint_stays_silent_without_empty_nudge()
    {
        var json = await RunSyntheticInitiativeAsync(
            SampleDefinitions.Examiner,
            silenceSeconds: 95,
            userText: "hmmm",
            assistantText: VietnamQuestion,
            speaksThisSilencePeriod: 1);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("staySilent", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public void Planner_context_serializes_note_as_structured_json()
    {
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(
            DateTimeOffset.UtcNow,
            silenceSeconds: 60,
            userText: "hmmm",
            assistantText: VietnamQuestion);
        var plan = InitiativePlan.Create(InitiativeIntent.Hint, "line one\nline two\"quoted\"");
        var request = builder.Build(context, Guid.NewGuid(), plan);
        var plannerContext = request.Messages.Single(message =>
            message.Role == ModelRole.User
            && message.Text.Contains("initiative_planner_observation", StringComparison.Ordinal)).Text;
        var jsonStart = plannerContext.IndexOf('{');
        Assert.True(jsonStart >= 0);
        using var doc = JsonDocument.Parse(plannerContext[jsonStart..]);
        Assert.Equal("initiative_planner_observation", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("line one\nline two\"quoted\"", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void InitiativePlan_create_rejects_empty_or_oversized_planner_note()
    {
        Assert.Throws<ArgumentException>(() => InitiativePlan.Create(InitiativeIntent.Hint, "   "));
        Assert.Throws<ArgumentException>(() => InitiativePlan.Create(InitiativeIntent.Hint, new string('x', 401)));
        Assert.False(InitiativePlan.TryCreate("hint", new string('y', 401), out _));
    }

    [Fact]
    public void InitiativePlan_create_rejects_undefined_enum_value()
    {
        var bogus = (InitiativeIntent)999;
        Assert.Throws<ArgumentException>(() => InitiativePlan.Create(bogus, "valid note."));
    }

    [Fact]
    public void Rephrase_plan_adds_simpler_formulation_guidance_to_generation()
    {
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(
            DateTimeOffset.UtcNow,
            silenceSeconds: 60,
            userText: "hmmm",
            assistantText: VietnamQuestion);
        var plan = InitiativePlan.Create(InitiativeIntent.Rephrase, "Simplify the Vietnam enjoyment question.");
        var request = builder.Build(context, Guid.NewGuid(), plan);
        var planSystem = request.Messages.Single(message =>
            message.Role == ModelRole.System
            && message.Text.Contains("Proactive initiative intent: rephrase", StringComparison.Ordinal)).Text;
        var plannerContext = request.Messages.Single(message =>
            message.Role == ModelRole.User
            && message.Text.Contains("untrusted observations", StringComparison.OrdinalIgnoreCase)).Text;
        Assert.Contains("simpler or clearer formulation", planSystem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simplify the Vietnam enjoyment question.", plannerContext, StringComparison.Ordinal);
        Assert.DoesNotContain("Simplify the Vietnam enjoyment question.", planSystem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluator_preserves_plan_on_speak_decision()
    {
        const string objective = "Offer one concrete hint about daily life in Vietnam.";
        var model = new FixedInitiativeModel(
            $$"""{"decision":"speak","intent":"hint","objective":"{{objective}}"}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion);
        var decision = await InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            Guid.NewGuid(),
            CancellationToken.None);
        var speak = Assert.IsType<Speak>(decision);
        Assert.NotNull(speak.Plan);
        Assert.Equal(InitiativeIntent.Hint, speak.Plan.Intent);
        Assert.Equal(objective, speak.Plan.PlannerNote);
        var plannerContext = speak.Request.Messages.Single(message =>
            message.Role == ModelRole.User
            && message.Text.Contains("untrusted observations", StringComparison.OrdinalIgnoreCase)).Text;
        Assert.Contains(objective, plannerContext, StringComparison.Ordinal);
        var framework = speak.Request.Messages.Single(message =>
            message.Text.Contains("Proactive initiative intent: hint", StringComparison.Ordinal)).Text;
        Assert.DoesNotContain(objective, framework, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_speak_without_objective_fails_closed()
    {
        var model = new FixedInitiativeModel("""{"decision":"speak","intent":"hint"}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion);
        var decision = await InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            Guid.NewGuid(),
            CancellationToken.None);
        var silent = Assert.IsType<StaySilent>(decision);
        Assert.False(silent.CountsTowardSilentCap);
        var entry = RuntimeTelemetry.SnapshotTimeline().Single(item =>
            item.Stage == "initiative_eval"
            && item.Detail!.Contains(context.Trigger.EventId.ToString(), StringComparison.Ordinal));
        Assert.Contains("\"reasonCode\":\"invalid_plan\"", entry.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speak_next_wait_ms_is_carried_on_decision()
    {
        var model = new FixedInitiativeModel(
            """{"decision":"speak","intent":"hint","objective":"Wait before next check.","nextWaitMs":45000}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion);
        var decision = await InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            Guid.NewGuid(),
            CancellationToken.None);
        var speak = Assert.IsType<Speak>(decision);
        Assert.Equal(45_000, speak.NextWaitMs);
    }

    [Fact]
    public async Task Deactivate_json_with_next_wait_ms_is_still_deactivate_without_wait()
    {
        var model = new FixedInitiativeModel(
            """{"decision":"deactivate","reason":"Nothing left to do.","nextWaitMs":45000}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion);
        var decision = await InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.IsType<RequestDeactivate>(decision);
    }

    [Fact]
    public async Task Invalid_next_wait_ms_is_treated_as_null()
    {
        var model = new FixedInitiativeModel(
            """{"decision":"staySilent","reason":"Hold.","nextWaitMs":-12}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion);
        var decision = await InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            Guid.NewGuid(),
            CancellationToken.None);
        var silent = Assert.IsType<StaySilent>(decision);
        Assert.Null(silent.NextWaitMs);
    }

    [Fact]
    public void Evaluator_prompt_omits_next_wait_for_deactivate()
    {
        var request = InitiativeEvaluator.CreateEvaluationRequest(
            ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion),
            new PromptContextBuilder());
        var system = request.Messages.First(message => message.Role == ModelRole.System).Text;
        Assert.Contains("staySilent", system, StringComparison.Ordinal);
        Assert.Contains("nextWaitMs is optional on staySilent and speak only", system, StringComparison.Ordinal);
        Assert.DoesNotContain("nextWaitMs is optional on every decision", system, StringComparison.Ordinal);
        Assert.DoesNotContain("staySilent/deactivate it delays", system, StringComparison.Ordinal);
        Assert.Contains("no next wait is armed", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_brain_hard_cap_blocks_evaluator_even_when_model_returns_speak()
    {
        var model = new ScriptedLanguageModel(
            ["""{"decision":"speak","intent":"hint","objective":"Should not run."}"""]);
        var builder = new PromptContextBuilder();
        var brain = new DefaultAgentBrain(builder, new DefaultInitiativeEvaluator(builder, model));
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, VietnamQuestion, "hmmm", speaksThisSilencePeriod: 1);
        var decision = await brain.DecideAsync(context, Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<StaySilent>(decision);
    }

    [Fact]
    public async Task Initiative_eval_telemetry_includes_decision_and_intent_without_objective_by_default()
    {
        var triggerId = Guid.Parse("019944af-0000-7000-8000-00000000a101");
        var model = new FixedInitiativeModel(
            """{"decision":"speak","intent":"hint","objective":"Internal only objective."}""");
        var builder = new PromptContextBuilder();
        var context = ExaminerContext(DateTimeOffset.UtcNow, 90, "hmmm", VietnamQuestion, triggerId: triggerId);
        _ = await InitiativeEvaluator.EvaluateAsync(model, builder, context, Guid.NewGuid(), CancellationToken.None);
        var entry = RuntimeTelemetry.SnapshotTimeline().Single(item =>
            item.Stage == "initiative_eval"
            && item.Detail!.Contains(triggerId.ToString(), StringComparison.Ordinal));
        Assert.Contains("\"decision\":\"speak\"", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("\"intent\":\"hint\"", entry.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("plannerNote", entry.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("objective", entry.Detail!, StringComparison.Ordinal);
    }

    private static SessionRuntime CreateExaminerRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time) =>
        Create(output, model, time, RecordingDefaultBrain(model), SampleDefinitions.Examiner);

    private static AgentContext ExaminerContext(
        DateTimeOffset now,
        double silenceSeconds,
        string userText,
        string assistantText,
        int speaksThisSilencePeriod = 0,
        Guid? triggerId = null) =>
        new(
            SampleDefinitions.Examiner,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    userText,
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    userText.Length,
                    userText.Length,
                    now.AddSeconds(-silenceSeconds - 5)),
                new ConversationEntry(
                    Guid.NewGuid(),
                    2,
                    Guid.NewGuid(),
                    ConversationRole.Assistant,
                    assistantText,
                    Guid.NewGuid(),
                    EntryStatus.Completed,
                    SessionMode.Text,
                    assistantText.Length,
                    assistantText.Length,
                    now.AddSeconds(-silenceSeconds - 2))
            ],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(triggerId ?? Guid.NewGuid(), TriggerKind.LongSilence, null),
            SpeaksThisSilencePeriod: speaksThisSilencePeriod,
            UtcNow: now,
            LastUserActivityAt: now.AddSeconds(-silenceSeconds));

    private static async Task<string> RunSyntheticInitiativeAsync(
        AgentDefinition definition,
        double silenceSeconds,
        string userText,
        string assistantText,
        int speaksThisSilencePeriod = 0)
    {
        var model = new ScriptedLanguageModel();
        var builder = new PromptContextBuilder();
        var now = DateTimeOffset.UtcNow;
        var context = new AgentContext(
            definition,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    userText,
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    userText.Length,
                    userText.Length,
                    now.AddSeconds(-silenceSeconds - 5)),
                new ConversationEntry(
                    Guid.NewGuid(),
                    2,
                    Guid.NewGuid(),
                    ConversationRole.Assistant,
                    assistantText,
                    Guid.NewGuid(),
                    EntryStatus.Completed,
                    SessionMode.Text,
                    assistantText.Length,
                    assistantText.Length,
                    now.AddSeconds(-silenceSeconds - 2))
            ],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.LongSilence, null),
            SpeaksThisSilencePeriod: speaksThisSilencePeriod,
            UtcNow: now,
            LastUserActivityAt: now.AddSeconds(-silenceSeconds));
        var request = InitiativeEvaluator.CreateEvaluationRequest(context, builder);
        var json = string.Empty;
        await foreach (var evt in model.GenerateAsync(request, CancellationToken.None))
        {
            if (evt is ModelTextDelta delta)
            {
                json += delta.Text;
            }
        }

        return json;
    }

    private static int CountStarted(CapturingSessionOutput output, string trigger) =>
        output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == trigger);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static RecordingAgentBrain RecordingDefaultBrain(ILanguageModel model) =>
        new(new DefaultAgentBrain(
            new PromptContextBuilder(),
            new DefaultInitiativeEvaluator(new PromptContextBuilder(), model)));

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        AgentDefinition? definition = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
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
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            brain,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
    }

    private sealed class FixedInitiativeModel(string json) : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(StreamingText: true, Cancellation: true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelTextDelta(json);
            yield return new ModelCompleted(ModelStopReason.Completed);
            await Task.CompletedTask;
        }
    }

    private sealed class CapturingGenerationModel(IReadOnlyList<string> initiativeJson, IReadOnlyList<string> generationChunks)
        : ILanguageModel
    {
        private readonly QueuedInitiativeLanguageModel _inner = new(initiativeJson, generationChunks);
        public string LastGenerationRequest { get; private set; } = string.Empty;

        public ModelCapabilities Capabilities => _inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
            if (system is null || !system.Contains(InitiativeEvaluator.Marker, StringComparison.Ordinal))
            {
                LastGenerationRequest = string.Join('\n', request.Messages.Select(message => message.Text));
            }

            await foreach (var evt in _inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
    }

    private sealed class QueuedInitiativeLanguageModel(IReadOnlyList<string> initiativeJson, IReadOnlyList<string> generationChunks)
        : ILanguageModel
    {
        private readonly ScriptedLanguageModel _generation = new(generationChunks);
        private int _initiativeCalls;

        public ModelCapabilities Capabilities => _generation.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
            if (system is not null && system.Contains(InitiativeEvaluator.Marker, StringComparison.Ordinal))
            {
                var index = Math.Min(_initiativeCalls++, initiativeJson.Count - 1);
                yield return new ModelTextDelta(initiativeJson[index]);
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            await foreach (var evt in _generation.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
    }
}
