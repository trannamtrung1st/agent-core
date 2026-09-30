using AgentCore.Application.Agents;
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
            ["workspace.read"],
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
}
