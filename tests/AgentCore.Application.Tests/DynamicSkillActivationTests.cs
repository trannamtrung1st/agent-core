using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class DynamicSkillActivationTests
{
    [Fact]
    public void Catalog_omits_inactive_procedures_and_loading_does_not_grant_tools()
    {
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"], ["vault/refund.md"], [ToolCatalog.WebSearch]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"], ["vault/order.md"], [ToolCatalog.WebSearch]));
        var context = Context(definition, ["refund.handle"]);
        var request = new PromptContextBuilder().Build(context, Guid.NewGuid());
        var catalog = Assert.Single(request.Messages, message =>
            message.Text.StartsWith(PromptContextBuilder.SkillCatalogPrefix, StringComparison.Ordinal));
        Assert.Contains("id: order.lookup", catalog.Text, StringComparison.Ordinal);
        Assert.Contains("requiredCapabilities: web.search", catalog.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", catalog.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND_PROCEDURE", catalog.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("vault/", request.Messages.Select(message => message.Text).Aggregate(string.Concat), StringComparison.Ordinal);
        var active = Assert.Single(request.Messages, PromptContextBuilder.IsActiveSkillSystem);
        Assert.Contains("REFUND_PROCEDURE", active.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", active.Text, StringComparison.Ordinal);

        var offered = new PromptContextBuilder().OfferTools(definition, context).Select(tool => tool.Name).ToArray();
        Assert.Equal([ToolCatalog.WorkspaceRead, ToolCatalog.SkillsLoad], offered);
        Assert.Equal(
            offered,
            new PromptContextBuilder().OfferTools(definition, Context(definition, ["refund.handle", "order.lookup"]))
                .Select(tool => tool.Name)
                .ToArray());
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.WebSearch,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.SkillsLoad,
                ToolConfigurationGates.Unconfigured,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.SkillsLoad,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(true, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.SkillsLoad,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.ScheduledOccurrence)));
        Assert.Equal(3, DeterministicSkillSelector.MaxActiveSkills);
        Assert.Equal(4, ConversationTurnExecution.MaxPinnedActiveSkills);

        var toolLess = Context(definition, ["refund.handle"]) with { ModelSupportsTools = false };
        var omitted = new PromptContextBuilder().Build(toolLess, Guid.NewGuid());
        Assert.DoesNotContain(
            omitted.Messages,
            message => message.Text.StartsWith(PromptContextBuilder.SkillCatalogPrefix, StringComparison.Ordinal)
                || message.Text.Contains("Load a Skill with skills.load", StringComparison.Ordinal));
        Assert.Contains(
            "REFUND_PROCEDURE",
            Assert.Single(omitted.Messages, PromptContextBuilder.IsActiveSkillSystem).Text,
            StringComparison.Ordinal);
        Assert.Empty(new PromptContextBuilder().OfferTools(definition, toolLess));
    }

    [Fact]
    public void Load_plan_rejects_unknown_draft_wrong_version_and_budget_failures()
    {
        var published = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"]),
            Skill("billing.note", "BILLING_PROCEDURE", ["billing"]),
            Skill("shipping.note", "SHIPPING_PROCEDURE", ["shipping"]),
            Skill("claims.note", new string('c', 5000), ["claims"]),
            Skill("returns.note", new string('r', 5000), ["returns"]));
        using var extra = JsonDocument.Parse("""{"ids":["order.lookup"],"sessionId":"nope"}""");
        Assert.False(SkillLoadAdmission.TryParseIds(extra.RootElement, out _, out var extraError));
        Assert.Contains("only ids", extraError, StringComparison.Ordinal);

        var missed = SkillLoadAdmission.Plan(
            published,
            ["refund.handle"],
            0,
            ["refund.handle", "order.lookup", "draft.secret"]);
        Assert.Equal(["order.lookup"], missed.Admitted);
        Assert.Equal(["refund.handle"], missed.AlreadyActive);
        Assert.Equal("unknown", Assert.Single(missed.Rejected).Reason);
        Assert.Equal("draft.secret", missed.Rejected[0].Id);
        Assert.DoesNotContain("ORDER_PROCEDURE", missed.ToToolResultJson(), StringComparison.Ordinal);

        var duplicate = SkillLoadAdmission.Plan(published, ["order.lookup"], 0, ["order.lookup"]);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Empty(duplicate.IdsToAppend);
        Assert.True(duplicate.IncrementInvocation);

        var overCount = SkillLoadAdmission.Plan(
            published,
            ["refund.handle", "order.lookup", "billing.note", "shipping.note"],
            0,
            ["claims.note"]);
        Assert.Equal("over_budget", Assert.Single(overCount.Rejected).Reason);
        Assert.Empty(overCount.IdsToAppend);

        var fourth = SkillLoadAdmission.Plan(
            published,
            ["refund.handle", "order.lookup", "billing.note"],
            0,
            ["shipping.note", "claims.note"]);
        Assert.Equal(["shipping.note"], fourth.Admitted);
        Assert.Equal("over_budget", Assert.Single(fourth.Rejected).Reason);

        var overCharacters = SkillLoadAdmission.Plan(published, [], 0, ["claims.note", "returns.note"]);
        Assert.Equal(["claims.note"], overCharacters.Admitted);
        Assert.Equal("returns.note", Assert.Single(overCharacters.Rejected).Id);
        Assert.Equal("over_budget", overCharacters.Rejected[0].Reason);

        var capped = SkillLoadAdmission.Plan(published, ["refund.handle"], SkillActivationLimits.MaxLoadInvocations, ["order.lookup"]);
        Assert.False(capped.IncrementInvocation);
        Assert.Equal("over_budget", capped.Outcome);
        Assert.Empty(capped.IdsToAppend);
    }

    [Fact]
    public void Admit_appends_to_the_same_pin_and_fences_stale_cancelled_and_terminal_executions()
    {
        var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var generation = Guid.Parse("019944af-0000-7000-8000-000000000091");
        var running = Running(now, generation, ["refund.handle"]);
        var loaded = running.AdmitActiveSkills(running.Revision, generation, ["order.lookup"], now.AddMinutes(1));
        Assert.Equal(["refund.handle", "order.lookup"], loaded.PinnedActiveSkillIds);
        Assert.Equal(1, loaded.SkillLoadCount);

        var duplicate = loaded.AdmitActiveSkills(loaded.Revision, generation, ["order.lookup"], now.AddMinutes(2));
        Assert.Equal(["refund.handle", "order.lookup"], duplicate.PinnedActiveSkillIds);
        Assert.Equal(2, duplicate.SkillLoadCount);

        var stale = Assert.Throws<WorkItemTransitionException>(() =>
            loaded.AdmitActiveSkills(loaded.Revision - 1, generation, ["billing.note"], now.AddMinutes(3)));
        Assert.Equal(WorkTransitionFailure.StaleRevision, stale.Failure);

        var cancelled = loaded.WithCancellationRequested(now.AddMinutes(3));
        var cancelledFailure = Assert.Throws<WorkItemTransitionException>(() =>
            cancelled.AdmitActiveSkills(cancelled.Revision, generation, ["billing.note"], now.AddMinutes(4)));
        Assert.Equal(WorkTransitionFailure.Illegal, cancelledFailure.Failure);

        var assistantId = Guid.Parse("019944af-0000-7000-8000-000000000092");
        var completed = loaded
            .WithAssistant(assistantId, now.AddMinutes(3))
            .CompleteTerminal(loaded.Revision + 1, generation, now.AddMinutes(4));
        var clearedClaim = Assert.Throws<WorkItemTransitionException>(() =>
            completed.AdmitActiveSkills(completed.Revision, generation, ["billing.note"], now.AddMinutes(5)));
        Assert.Equal(WorkTransitionFailure.StaleGeneration, clearedClaim.Failure);
        var terminalExecution = ConversationTurnExecution.Restore(
            loaded.ExecutionId,
            loaded.SessionId,
            loaded.SourceUserEntryId,
            loaded.SourceEventId,
            loaded.ResponseId,
            loaded.AgentInstanceId,
            loaded.ProfileId,
            loaded.DefinitionId,
            loaded.DefinitionVersion,
            loaded.PinnedPersona,
            loaded.PinnedModel,
            ConversationTurnExecutionStatus.Completed,
            loaded.Revision,
            loaded.Claim,
            assistantId,
            false,
            null,
            loaded.AcceptedAtUtc,
            now.AddMinutes(4),
            loaded.PinnedActiveSkillIds,
            loaded.SkillLoadCount);
        var terminal = Assert.Throws<WorkItemTransitionException>(() =>
            terminalExecution.AdmitActiveSkills(terminalExecution.Revision, generation, ["billing.note"], now.AddMinutes(5)));
        Assert.Equal(WorkTransitionFailure.Terminal, terminal.Failure);
        Assert.Equal(["refund.handle", "order.lookup"], completed.PinnedActiveSkillIds);
        Assert.Equal(1, completed.SkillLoadCount);
    }

    [Fact]
    public async Task Direct_tool_execution_does_not_apply_skill_load()
    {
        var definition = Definition(Skill("order.lookup", "ORDER_PROCEDURE", ["order"]));
        var executor = new SessionToolExecutor();
        var result = await executor.ExecuteAsync(
            definition,
            Guid.NewGuid(),
            new ModelToolCall("call-1", ToolCatalog.SkillsLoad, """{"ids":["order.lookup"]}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        Assert.Contains("owned by the session runtime", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dynamic_load_admits_a_skill_the_selector_missed_and_stops_at_the_invocation_cap()
    {
        var model = new SkillLoadLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"], ["vault/refund.md"], [ToolCatalog.WebSearch]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"], ["vault/order.md"], [ToolCatalog.WebSearch]),
            Skill("shipping.note", "SHIPPING_PROCEDURE", ["shipping"], ["vault/shipping.md"])));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(4, model.Requests.Count);
        var first = Text(model.Requests[0]);
        Assert.Contains("id: order.lookup", first, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_PROCEDURE", first, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND_PROCEDURE", first, StringComparison.Ordinal);
        Assert.DoesNotContain("SHIPPING_PROCEDURE", first, StringComparison.Ordinal);
        Assert.DoesNotContain("vault/", first, StringComparison.Ordinal);
        Assert.Contains(ToolCatalog.SkillsLoad, model.Requests[0].Tools!.Select(tool => tool.Name));
        Assert.Contains(ToolCatalog.WorkspaceRead, model.Requests[0].Tools!.Select(tool => tool.Name));
        Assert.DoesNotContain(ToolCatalog.WebSearch, model.Requests[0].Tools!.Select(tool => tool.Name));

        var continuation = Text(model.Requests[1]);
        Assert.Contains("ORDER_PROCEDURE", continuation, StringComparison.Ordinal);
        Assert.DoesNotContain("SHIPPING_PROCEDURE", continuation, StringComparison.Ordinal);
        Assert.DoesNotContain("vault/", continuation, StringComparison.Ordinal);
        Assert.Equal(
            model.Requests[0].Tools!.Select(tool => tool.Name).ToArray(),
            model.Requests[1].Tools!.Select(tool => tool.Name).ToArray());
        Assert.DoesNotContain(ToolCatalog.WebSearch, model.Requests[1].Tools!.Select(tool => tool.Name));
        Assert.Equal(1, Count(Text(model.Requests[2]), "ORDER_PROCEDURE"));
        Assert.Contains("over_budget", Text(model.Requests[3]), StringComparison.Ordinal);
        Assert.DoesNotContain("SHIPPING_PROCEDURE", Text(model.Requests[3]), StringComparison.Ordinal);

        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        var pinned = await turns.GetBySourceEventAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Equal(["order.lookup"], pinned!.PinnedActiveSkillIds);
        Assert.Equal(2, pinned.SkillLoadCount);
        Assert.Equal(
            "Shown",
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed).Text);
    }

    [Fact]
    public async Task No_tools_model_omits_continuation_capabilities_and_still_preloads()
    {
        var model = new TerminalLanguageModel();
        var turns = new InMemoryConversationTurnExecutionStore();
        await using var runtime = Create(model, turns, Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"])));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("check the order");
        await runtime.WaitUntilIdleAsync();

        var request = Assert.Single(model.Requests);
        Assert.True(request.Tools is null || request.Tools.Count == 0);
        Assert.DoesNotContain(
            request.Tools ?? [],
            tool => tool.Name == ToolCatalog.SkillsLoad);
        var text = Text(request);
        Assert.Contains("ORDER_PROCEDURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND_PROCEDURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptContextBuilder.SkillCatalogPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Load a Skill with skills.load", text, StringComparison.Ordinal);
        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        var pinned = await turns.GetBySourceEventAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Equal(["order.lookup"], pinned!.PinnedActiveSkillIds);
        Assert.Equal(0, pinned.SkillLoadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_reads_the_stored_pin_and_does_not_reselect_keywords(bool capabilities)
    {
        var model = new TerminalLanguageModel(capabilities);
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var definition = Definition(
            Skill("refund.handle", "REFUND_PROCEDURE", ["refund"]),
            Skill("order.lookup", "ORDER_PROCEDURE", ["order"]));
        if (capabilities) definition = definition with { Environment = new(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead]), Projection: new([])) };
        var sessionId = ids.NewSessionId();
        var userId = Guid.Parse("019944af-00aa-7000-8000-0000000000aa");
        var user = new ConversationEntry(
            userId,
            1,
            userId,
            ConversationRole.User,
            "please refund this",
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            "please refund this".Length,
            "please refund this".Length,
            now);
        var snapshot = new SessionSnapshot(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [user],
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
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(snapshot, 0);
        var created = await turns.CreateAsync(ConversationTurnExecution.AcceptNew(
            Guid.Parse("019944af-00aa-7000-8000-0000000000ab"),
            sessionId,
            userId,
            userId,
            Guid.Parse("019944af-00aa-7000-8000-0000000000ac"),
            null,
            null,
            definition.Id,
            definition.Version,
            null,
            new WorkModelPin("synthetic-offline/scripted", "primary-llm", "scripted", null),
            now,
            []));
        var generation = Guid.Parse("019944af-00aa-7000-8000-0000000000ad");
        var claimed = await turns.TryClaimAsync(created.Item.ExecutionId, generation, now, now.AddSeconds(1));
        var admitted = await turns.AdmitActiveSkillsAsync(
            claimed!.ExecutionId,
            claimed.Revision,
            generation,
            ["order.lookup"],
            now.AddSeconds(1));
        if (capabilities) admitted = await turns.AdmitCapabilitiesAsync(admitted.ExecutionId, admitted.Revision, generation, [ToolCatalog.WorkspaceRead], now.AddSeconds(1));
        Assert.Equal(1, await turns.RecoverExpiredClaimsAsync(now.AddMinutes(1)));
        Assert.Equal(["order.lookup"], admitted.PinnedActiveSkillIds);

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
        await runtime.AttachAsync();
        await runtime.WaitUntilIdleAsync();

        var text = Text(Assert.Single(model.Requests));
        Assert.Contains("ORDER_PROCEDURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND_PROCEDURE", text, StringComparison.Ordinal);
        var reloaded = await turns.GetBySourceEventAsync(sessionId, userId);
        Assert.Equal(["order.lookup"], reloaded!.PinnedActiveSkillIds);
        Assert.Equal(1, reloaded.SkillLoadCount);
        if (capabilities)
        {
            Assert.Equal([ToolCatalog.WorkspaceRead], reloaded.LoadedCapabilityIds);
            Assert.Equal(1, reloaded.CapabilityLoadCount);
            Assert.Contains(Assert.Single(model.Requests).Tools!, t => t.Name == ToolCatalog.WorkspaceRead);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_capability_load_cannot_mutate_cancelled_or_superseded_response(bool supersede)
    {
        var definition = Definition() with { Environment = new(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead]), Projection: new([])) };
        var turns = new InMemoryConversationTurnExecutionStore();
        var model = new LateCapabilityModel();
        await using var runtime = Create(model, turns, definition);
        await runtime.AttachAsync();
        var first = Guid.NewGuid();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("first", first));
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var execution = (await turns.GetBySourceEventAsync(runtime.SessionId, first))!;
        var second = Guid.NewGuid();
        if (supersede) Assert.True(await runtime.SubmitPersistedUserTextAsync("second", second));
        else await runtime.CancelResponseAsync(execution.ResponseId);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var stale = (await turns.GetBySourceEventAsync(runtime.SessionId, first))!;
        Assert.Empty(stale.LoadedCapabilityIds);
        Assert.Equal(0, stale.CapabilityLoadCount);
        if (supersede)
        {
            var current = (await turns.GetBySourceEventAsync(runtime.SessionId, second))!;
            Assert.Empty(current.LoadedCapabilityIds);
            Assert.Equal(0, current.CapabilityLoadCount);
            Assert.DoesNotContain(ToolCatalog.WorkspaceRead, model.CurrentTools!);
        }
    }
    private sealed class LateCapabilityModel : ILanguageModel
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string[]? CurrentTools { get; private set; }
        private int _calls;
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task; // Deliberately non-cooperative late provider callback.
                yield return new ModelToolCallEvent(new("late-load", ToolCatalog.CapabilitiesLoad, "{\"query\":\"workspace.read\"}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                CurrentTools = request.Tools?.Select(t => t.Name).ToArray();
                yield return new ModelDisplayDelta("Current response");
                yield return new ModelSemanticResponseReady(new("Current response", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    private static string Text(ModelRequest request) =>
        string.Join('\n', request.Messages.Select(message => message.Text));

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        IReadOnlyList<string>? resources = null,
        IReadOnlyList<string>? capabilities = null) =>
        new(id, id, "", procedure, keywords, capabilities ?? [], resources ?? []);

    private static ConversationTurnExecution Running(DateTimeOffset now, Guid generation, IReadOnlyList<string> pinned) =>
        ConversationTurnExecution.AcceptNew(
                Guid.Parse("019944af-0000-7000-8000-000000000081"),
                Guid.Parse("019944af-0000-7000-8000-000000000082"),
                Guid.Parse("019944af-0000-7000-8000-000000000083"),
                Guid.Parse("019944af-0000-7000-8000-000000000084"),
                Guid.Parse("019944af-0000-7000-8000-000000000085"),
                null,
                null,
                "examiner",
                1,
                null,
                new WorkModelPin("synthetic-offline/scripted", "primary-llm", "scripted", null),
                now,
                pinned)
            .TakeClaim(generation, now, now.AddMinutes(5));

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
                null), AgentInstanceId: Guid.NewGuid());
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

    private sealed class SkillLoadLanguageModel : ILanguageModel
    {
        private int _generation;

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var generation = Interlocked.Increment(ref _generation);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (generation == 1)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "load-1",
                    ToolCatalog.SkillsLoad,
                    """{"ids":["order.lookup"]}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            if (generation == 2)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "load-2",
                    ToolCatalog.SkillsLoad,
                    """{"ids":["order.lookup"]}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            if (generation == 3)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "load-3",
                    ToolCatalog.SkillsLoad,
                    """{"ids":["shipping.note"]}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class TerminalLanguageModel(bool tools = false) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: tools);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
