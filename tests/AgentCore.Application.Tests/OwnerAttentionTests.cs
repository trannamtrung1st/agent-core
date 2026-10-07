using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class OwnerAttentionTests
{
    [Fact]
    public async Task Quiet_text_completion_stores_no_alert()
    {
        var store = new InMemoryWorkItemStore();
        var outcome = await RunAsync(
            store,
            ToolRound(Call(ToolCatalog.WorkComplete, """{"summary":"Nothing needs attention.","attentionRequired":false,"outcome":"ActionCompleted"}""")));
        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.False(completed.AttentionRequired);
        var saved = await store.CompleteAsync(
            completed.Running.WorkItemId,
            completed.Running.Revision,
            Generation,
            completed.Text,
            Now,
            attentionRequired: completed.AttentionRequired);
        Assert.False(saved.Result!.AttentionRequired);
        Assert.Empty(await store.ListAttentionAlertKeysAsync(saved.WorkItemId));
    }

    [Fact]
    public async Task Attention_completion_is_one_alert_key_after_recovery()
    {
        var store = new InMemoryWorkItemStore();
        var outcome = await RunAsync(
            store,
            ToolRound(Call(
                ToolCatalog.WorkComplete,
                """{"summary":"Two orders need review.","attentionRequired":true,"outcome":"AttentionRequested"}""")));
        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.True(completed.AttentionRequired);
        Assert.Equal("Two orders need review.", WorkCompletionRequest.Summary(completed.Text));
        var saved = await store.CompleteAsync(
            completed.Running.WorkItemId,
            completed.Running.Revision,
            Generation,
            completed.Text,
            Now,
            attentionRequired: completed.AttentionRequired);
        var key = WorkAttentionKey.Format(saved.WorkItemId, saved.Revision);
        Assert.Equal([key], await store.ListAttentionAlertKeysAsync(saved.WorkItemId));
        var recovered = await store.CompleteAsync(
            saved.WorkItemId,
            saved.Revision,
            Generation,
            completed.Text,
            Now.AddMinutes(1),
            attentionRequired: true);
        Assert.Equal(saved.Revision, recovered.Revision);
        Assert.False(await store.TryRecordAttentionAlertAsync(saved.WorkItemId, saved.Revision, Now.AddMinutes(2)));
        Assert.Equal([key], await store.ListAttentionAlertKeysAsync(saved.WorkItemId));
    }

    [Fact]
    public async Task Recipient_field_is_rejected_and_detached_messaging_stays_denied()
    {
        var store = new InMemoryWorkItemStore();
        var rejected = await RunAsync(
            store,
            ToolRound(Call(
                ToolCatalog.WorkComplete,
                """{"summary":"Tell Sam.","attentionRequired":true,"recipient":"sam@example.com"}""")));
        var failed = Assert.IsType<DurableOccurrenceFailed>(rejected);
        Assert.Equal("invalid-completion", failed.Code);
        Assert.Contains("completion", failed.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.ListAttentionAlertKeysAsync(WorkId));
        Assert.Null((await store.GetAsync(new WorkOwner(OwnerId, ProfileId), WorkId))!.Result);

        var definition = Definition();
        var schema = ToolRegistry.Get(ToolCatalog.WorkComplete).ModelDefinition.ParametersJson;
        Assert.DoesNotContain("recipient", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.WorkComplete,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.WorkComplete,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(
                    true,
                    TriggerKind.ScheduledOccurrence,
                    AgentInstanceId: OwnerId)));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.BrowserNavigate,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(
                    true,
                    TriggerKind.ScheduledOccurrence,
                    AgentInstanceId: null)));
        var messaging = await new SessionToolExecutor().ExecuteAsync(
            definition,
            Guid.Empty,
            Call(ToolCatalog.AppMessageSend, """{"text":"hello"}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: OwnerId));
        Assert.Contains("forbidden", messaging.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Occurrence_offers_work_complete_without_a_recipient_and_browser_requires_owner()
    {
        var brain = new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll));
        var connected = Context(trusted: true);
        var speak = Assert.IsType<Speak>(await brain.DecideAsync(connected, Guid.NewGuid()));
        Assert.Contains(speak.Request.Tools!, tool => tool.Name == ToolCatalog.WorkComplete);
        Assert.DoesNotContain(
            "recipient",
            speak.Request.Tools!.Single(tool => tool.Name == ToolCatalog.WorkComplete).ParametersJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(speak.Request.Tools!, tool => tool.Name == ToolCatalog.BrowserNavigate);
        var unconnected = Assert.IsType<Speak>(await brain.DecideAsync(Context(trusted: false), Guid.NewGuid()));
        Assert.Contains(unconnected.Request.Tools!, tool => tool.Name == ToolCatalog.WorkComplete);
        Assert.DoesNotContain(unconnected.Request.Tools!, tool => ToolCatalog.IsBrowserTool(tool.Name));
    }

    [Fact]
    public async Task Scheduled_occurrence_without_a_store_connection_can_require_attention()
    {
        var store = new InMemoryWorkItemStore();
        var outcome = await RunAsync(
            store,
            ToolRound(Call(
                ToolCatalog.WorkComplete,
                """{"summary":"A payment failed.","attentionRequired":true,"outcome":"AttentionRequested"}""")));
        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.True(completed.AttentionRequired);
        Assert.Equal("A payment failed.", WorkCompletionRequest.Summary(completed.Text));
    }

    private static async Task<DurableOccurrenceOutcome> RunAsync(
        InMemoryWorkItemStore store,
        IReadOnlyList<ModelGenerationEvent> round)
    {
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            new WorkProvenance(
                Guid.Parse("019944af-00f2-7000-8000-000000000005"),
                WorkSourceKind.Schedule,
                null,
                null,
                null,
                "source|attention",
                Now,
                Now,
                """{"instruction":"synthetic"}""",
                "general-assistant",
                11,
                "Test"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            Now));
        var claimed = (await store.TryClaimAsync(WorkId, Generation, Now, Now.AddMinutes(5)))!;
        return await new DurableOccurrenceExecution(new SessionToolExecutor(), TimeProvider.System).RunAsync(
            claimed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "review the store")]),
            new ScriptModel(round),
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, Generation, body, null, Now, token),
            store,
            Generation,
            Now,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00f3-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940f301")]),
            CancellationToken.None);
    }

    private static AgentDefinition Definition() =>
        new(
            1,
            "general-assistant",
            11,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserObserve,
                ToolCatalog.BrowserAct,
                ToolCatalog.AppMessageSend
            ]));

    private static AgentContext Context(bool trusted) =>
        new(
            Definition(),
            [],
            "",
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.ScheduledOccurrence, "review"),
            DetachedExecution: true,
            AgentInstanceId: trusted ? OwnerId : null);

    private static ModelToolCall Call(string name, string arguments) => new("call-" + name, name, arguments);

    private static IReadOnlyList<ModelGenerationEvent> TextRound(string text) =>
    [
        new ModelTextDelta(text),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static IReadOnlyList<ModelGenerationEvent> ToolRound(ModelToolCall call) =>
    [
        new ModelToolCallEvent(call),
        new ModelCompleted(ModelStopReason.ToolCalls)
    ];

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.Parse("019944af-00f2-7000-8000-000000000003");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00f2-7000-8000-000000000004");
    private static readonly Guid WorkId = Guid.Parse("019944af-00f2-7000-8000-000000000002");
    private static readonly Guid Generation = Guid.Parse("019944af-00f2-7000-8000-000000000001");

    private sealed class ScriptModel(IReadOnlyList<ModelGenerationEvent> round) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in round)
            {
                yield return item;
            }
        }
    }
}
