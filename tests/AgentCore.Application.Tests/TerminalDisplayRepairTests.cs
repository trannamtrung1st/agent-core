using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TerminalDisplayRepairTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Substantial_workspace_document_materializes_and_follow_up_note_repairs_invalid_blocks(bool structured)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-core-block-repair-{Guid.NewGuid():N}");
        try
        {
            var workspace = new FileSessionWorkspace(Path.Combine(root, "ws"), Path.Combine(root, "templates"));
            var artifacts = new InMemoryArtifactStore(TimeProvider.System);
            var document = string.Concat(Enumerable.Repeat("# Scrum playbook\nObserve the current evidence before making changes.\n", 1_500));
            var inner = new DocumentModel(document, structured);
            var model = new SemanticResponseLanguageModel(inner);
            var executor = new SessionToolExecutor(workspace: workspace, agentWorkspace: OwnedWorkspaces.Create(workspace), artifacts: artifacts, configurationGate: ToolConfigurationGates.AllowAll);
            await using var runtime = CreateCore(model, null, null,
                [ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreateFromWorkspace], executor, new SessionArtifactAuthorizer(artifacts));
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Create a substantial Scrum playbook and materialize it."));
            await runtime.WaitUntilIdleAsync();
            var first = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.Assistant);
            Assert.Equal(EntryStatus.Completed, first.Status);
            var artifact = Assert.Single(await artifacts.ListAsync(runtime.SessionId));
            Assert.Equal(artifact.ArtifactId.ToString(), Assert.Single(first.Envelope!.Blocks).ArtifactId);
            Assert.True(artifact.ByteSize > 64 * 1024);
            Assert.DoesNotContain("[[", first.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("[Unavailable artifact]", first.Text, StringComparison.Ordinal);
            Assert.True(await runtime.SubmitUserTextAsync("Write this down as a note first."));
            await runtime.WaitUntilIdleAsync();
            var answers = runtime.Snapshot.Entries.Where(e => e.Role == ConversationRole.Assistant).ToArray();
            Assert.Equal(2, answers.Length);
            Assert.All(answers, e => Assert.Equal(EntryStatus.Completed, e.Status));
            Assert.Equal("Saved the note in notes.md.", answers[1].Text);
            Assert.Empty(answers[1].Envelope!.Blocks);
            Assert.Equal(document, Encoding.UTF8.GetString((await workspace.ReadAsync(runtime.SessionId,
                runtime.Snapshot.Definition, "/workspace/working/notes.md")).Bytes));
            Assert.Single(await artifacts.ListAsync(runtime.SessionId));
            Assert.Equal([ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreateFromWorkspace, ToolCatalog.WorkspaceWrite], inner.ToolCalls);
            Assert.Equal(6, inner.Requests.Count);
            AssertTerminalChannelOnly(inner.Requests[^1]);
            Assert.Contains(inner.Requests[^1].Messages, m => m.Text == ProtocolFailures.InvalidBlocksInstruction);
            Assert.Contains(inner.Requests[^1].Messages, m => m.Role == ModelRole.Tool && m.Text.Contains("notes.md", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProviderFailureReason.MissingDisplayText)]
    [InlineData(ProviderFailureReason.InvalidBlocks)]
    public async Task Terminal_presentation_failure_repairs_once_without_offering_browser_tools(string reason)
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                new ModelToolCallEvent(new ModelToolCall("obs-1", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [Invalid(reason)],
            Answer("Zigwheels lists the price."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate, ToolCatalog.BrowserObserve);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(3, model.Calls);
        Assert.Equal(["https://zigwheels.test/"], browser.Navigated);
        Assert.Equal(1, browser.ObserveCalls);
        var repair = model.Requests[2];
        AssertTerminalChannelOnly(repair);
        Assert.Contains(repair.Messages, message => message.Text == ProtocolFailures.RepairInstruction(reason));
        Assert.Contains(
            repair.Messages,
            message => message.Role == ModelRole.Tool
                && message.Text.Contains("zigwheels.test", StringComparison.Ordinal));
        Assert.Equal(8192, repair.MaxOutputTokens);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Zigwheels lists the price.", assistant.Text);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains("application protocol", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_direct_chat_shows_only_the_repaired_answer()
    {
        var model = new RecordingModel(
            [Invalid(ProviderFailureReason.MissingDisplayText)],
            Answer("The repaired answer."));
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, model.Calls);
        AssertTerminalChannelOnly(model.Requests[1]);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("The repaired answer.", assistant.Text);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains("application protocol", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProviderFailureReason.MissingDisplayText)]
    [InlineData(ProviderFailureReason.InvalidBlocks)]
    public async Task A_repair_completion_without_a_semantic_response_records_failure(string reason)
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name == "llm.response.repair")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Add(tag.Value?.ToString() ?? "");
                }
            }
        });
        listener.Start();
        var model = new RecordingModel(
            [Invalid(reason)],
            [new ModelCompleted(ModelStopReason.Completed)]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await runtime.WaitUntilIdleAsync();
        listener.Dispose();

        Assert.Equal(2, model.Calls);
        Assert.Equal(
            EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
        Assert.Equal(["started", "failed"], outcomes);
        Assert.Equal(reason, runtime.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant).Failure!.FailureReason);
    }

    [Theory]
    [InlineData(ProviderFailureReason.MissingDisplayText)]
    [InlineData(ProviderFailureReason.InvalidBlocks)]
    public async Task A_second_presentation_failure_is_terminal(string reason)
    {
        var model = new RecordingModel(
            [Invalid(reason)],
            [Invalid(reason)]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, model.Calls);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.Equal("attempted", assistant.Failure!.ProtocolRepair);
        Assert.Equal("failed", assistant.Failure.ProtocolRepairOutcome);
        Assert.Equal(reason, assistant.Failure.FailureReason);
    }

    [Theory]
    [InlineData(ProviderFailureReason.UnknownAction)]
    [InlineData(ProviderFailureReason.ModelSuppliedDestination)]
    [InlineData(ProviderFailureReason.InvalidMemory)]
    [InlineData(ProviderFailureReason.InvalidMemoryProposal)]
    public async Task Non_repairable_invalid_responses_fail_immediately(string reason)
    {
        var model = new RecordingModel([Invalid(reason)]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, model.Calls);
        Assert.Equal(
            EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Published_output_prevents_display_repair()
    {
        var model = new RecordingModel(
            [
                new ModelSemanticResponseReady(new ModelSemanticResponse(
                    "Already visible",
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    [])),
                Invalid(ProviderFailureReason.MissingDisplayText)
            ]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task An_admitted_tool_call_prevents_display_repair()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                Invalid(ProviderFailureReason.MissingDisplayText)
            ]);
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, model.Calls);
        Assert.Empty(browser.Navigated);
    }

    [Fact]
    public async Task A_tool_call_during_repair_is_not_executed()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [Invalid(ProviderFailureReason.MissingDisplayText)],
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ]);
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, model.Calls);
        Assert.Empty(browser.Navigated);
        Assert.Equal(
            EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Cancellation_during_display_repair_stops_immediately()
    {
        var model = new CancelOnRepairModel();
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await model.RepairStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.CancelActiveResponseAsync();
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, model.Calls);
        Assert.Equal(
            EntryStatus.Interrupted,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Cancellation_before_repair_does_not_start_one()
    {
        var model = new CancelBeforeRepairModel();
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await model.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.CancelActiveResponseAsync();
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, model.Calls);
        Assert.Equal(
            EntryStatus.Interrupted,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_committed_close_receipt_survives_protocol_repair(bool repairSucceeds)
    {
        var browser = new CountingBrowser();
        var model = repairSucceeds
            ? new RecordingModel(
                [
                    new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "{}")),
                    new ModelCompleted(ModelStopReason.ToolCalls)
                ],
                [Invalid(ProviderFailureReason.MissingDisplayText)],
                Answer("The browser is closed."))
            : new RecordingModel(
                [
                    new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "{}")),
                    new ModelCompleted(ModelStopReason.ToolCalls)
                ],
                [Invalid(ProviderFailureReason.MissingDisplayText)],
                [Invalid(ProviderFailureReason.MissingDisplayText)]);
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("close the browser"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, browser.CloseCalls);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(repairSucceeds ? EntryStatus.Completed : EntryStatus.Failed, assistant.Status);
        Assert.Equal("Browser closed", Assert.Single(assistant.Envelope!.EffectReceipts!).Label);
        if (repairSucceeds)
        {
            Assert.Equal("The browser is closed.", assistant.Text);
        }
    }

    [Fact]
    public async Task A_transient_failure_during_repair_retries_without_tools_or_a_second_effect()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [
                new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [Invalid(ProviderFailureReason.MissingDisplayText)],
            [Unavailable(ProviderFailureReason.StreamIncomplete)],
            Answer("The browser is closed."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("close the browser"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(4, model.Calls);
        Assert.Equal(1, browser.CloseCalls);
        AssertTerminalChannelOnly(model.Requests[2]);
        AssertTerminalChannelOnly(model.Requests[3]);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Browser closed", Assert.Single(assistant.Envelope!.EffectReceipts!).Label);
    }

    [Fact]
    public async Task A_transient_retry_does_not_consume_protocol_repair()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [Unavailable(ProviderFailureReason.StreamIncomplete)],
            [Invalid(ProviderFailureReason.MissingDisplayText)],
            Answer("Zigwheels lists the price."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(4, model.Calls);
        Assert.Equal(["https://zigwheels.test/"], browser.Navigated);
        Assert.Contains(model.Requests[2].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserNavigate);
        AssertTerminalChannelOnly(model.Requests[3]);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task An_expired_execution_deadline_does_not_start_protocol_repair()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var browser = new CountingBrowser();
        var model = new HoldThenFailModel();
        await using var runtime = Create(model, browser, clock, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await model.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(301));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, model.Calls);
        Assert.Single(browser.Navigated);
        Assert.NotEqual(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Theory]
    [InlineData(ProviderFailureReason.SpeechOmitted, ProtocolFailureDisposition.Normalize)]
    [InlineData(ProviderFailureReason.SpeechMalformed, ProtocolFailureDisposition.Normalize)]
    [InlineData(ProviderFailureReason.MissingDisplayText, ProtocolFailureDisposition.Repairable)]
    [InlineData(ProviderFailureReason.InvalidBlocks, ProtocolFailureDisposition.Repairable)]
    [InlineData(ProviderFailureReason.UnknownAction, ProtocolFailureDisposition.Terminal)]
    [InlineData(ProviderFailureReason.ModelSuppliedDestination, ProtocolFailureDisposition.Terminal)]
    [InlineData(ProviderFailureReason.InvalidMemory, ProtocolFailureDisposition.Terminal)]
    [InlineData(ProviderFailureReason.InvalidMemoryProposal, ProtocolFailureDisposition.Terminal)]
    [InlineData(ProviderFailureReason.ResponseTooLarge, ProtocolFailureDisposition.Terminal)]
    public void Protocol_failure_disposition_is_an_explicit_allowlist(string reason, ProtocolFailureDisposition disposition)
    {
        Assert.Equal(disposition, ProtocolFailures.Disposition(reason));
        if (disposition == ProtocolFailureDisposition.Repairable)
        {
            Assert.Contains("return only the final user-visible answer text", ProtocolFailures.RepairInstruction(reason), StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(ProtocolFailures.RepairInstruction(reason));
        }
    }

    private static ModelFailed Invalid(string reason) =>
        new(new ProviderFailure(
            ProviderErrorCode.InvalidResponse,
            "Malformed assistant envelope.",
            FailureReason: reason));

    private static ModelFailed Unavailable(string reason) =>
        new(new ProviderFailure(
            ProviderErrorCode.Unavailable,
            "Language model is unavailable.",
            FailureReason: reason));

    private static void AssertTerminalChannelOnly(ModelRequest request)
    {
        Assert.Null(request.ResponseContract);
        Assert.Null(request.Tools);
        Assert.Contains(
            request.Messages,
            message => message.Role == ModelRole.System
                && message.Text.Contains("Do not emit JSON", StringComparison.Ordinal));
        Assert.DoesNotContain(
            request.Tools ?? [],
            tool => tool.Name is ToolCatalog.BrowserNavigate
                or ToolCatalog.BrowserObserve
                or ToolCatalog.BrowserAct
                or ToolCatalog.BrowserClose
                or ToolCatalog.EmailSend
                or ToolCatalog.AppMessageSend
                or ToolCatalog.SkillsLoad);
    }

    private static ModelGenerationEvent[] Answer(string text) =>
    [
        new ModelTextDelta(text),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static SessionRuntime CreateCore(
        ILanguageModel model,
        IBrowserSession? browser,
        FakeTimeProvider? clock,
        string[] tools,
        SessionToolExecutor? executor = null,
        IArtifactReferenceAuthorizer? artifactAuthorizer = null)
    {
        var time = clock ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00f3-7000-8000-{index:D12}")),
            [Guid.NewGuid()]);
        var now = time.GetUtcNow();
        var definition = new AgentDefinition(
            1,
            "general-assistant",
            12,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist: tools));
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
        var store = new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            artifacts: artifactAuthorizer,
            tools: executor ?? new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll));
    }

    private static SessionRuntime Create(ILanguageModel model, IBrowserSession? browser, FakeTimeProvider? clock, params string[] tools) =>
        CreateCore(model, browser, clock, tools);

    private static SessionRuntime Create(ILanguageModel model, IBrowserSession? browser, params string[] tools) =>
        Create(model, browser, clock: null, tools);

    private sealed class DocumentModel(string document, bool structured) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: structured);
        public List<ModelRequest> Requests { get; } = [];
        public List<string> ToolCalls { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            ModelToolCall? call = Requests.Count switch
            {
                1 => new("document", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/playbook.md", content = document })),
                2 => new("artifact", ToolCatalog.ArtifactsCreateFromWorkspace, """{"path":"/working/playbook.md","displayName":"Scrum playbook.md"}"""),
                4 => new("note", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/notes.md", content = document })),
                _ => null
            };
            if (call is not null)
            {
                ToolCalls.Add(call.Name);
                yield return new ModelToolCallEvent(call);
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                string text;
                if (Requests.Count == 3)
                {
                    using var result = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                    var id = result.RootElement.GetProperty("artifactId").GetString();
                    text = JsonSerializer.Serialize(new { disposition = "Complete", action = new { kind = "chat.respond" },
                        displayText = "Saved the playbook.", speech = new { mode = "same" }, memory = Array.Empty<object>(),
                        blocks = new[] { new { kind = "artifactReference", artifactId = id } } });
                }
                else if (Requests.Count == 5)
                    text = """{"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"Saved the note.","speech":{"mode":"same"},"memory":[],"blocks":[{"kind":"widget"}]}""";
                else
                    text = "Saved the note in notes.md.";
                if (!structured && Requests.Count is 3 or 5)
                {
                    yield return new ModelToolCallEvent(new("response", AssistantResponseSchema.ResponseFunctionName, text));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                }
                else
                {
                    yield return new ModelTextDelta(text);
                    yield return new ModelCompleted(ModelStopReason.Completed);
                }
            }
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingModel(params ModelGenerationEvent[][] steps) : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Requests.Add(request);
            var call = Interlocked.Increment(ref _calls);
            foreach (var evt in steps[call - 1])
            {
                yield return evt;
            }
        }
    }

    private sealed class CancelOnRepairModel : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource RepairStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                yield return new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "Malformed assistant envelope.",
                    FailureReason: ProviderFailureReason.MissingDisplayText));
                yield break;
            }

            RepairStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CancelBeforeRepairModel : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class HoldThenFailModel : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            Holding.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelFailed(new ProviderFailure(
                ProviderErrorCode.InvalidResponse,
                "Malformed assistant envelope.",
                FailureReason: ProviderFailureReason.MissingDisplayText));
        }
    }

    private sealed class CountingBrowser : IBrowserSession
    {
        public List<string> Navigated { get; } = [];

        public int ObserveCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["https://zigwheels.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(Navigated.Count == 0 ? null : new Uri(Navigated[^1]));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated.Add(request.Url!.AbsoluteUri);
            return new(new BrowserOperationResult(
                null,
                new BrowserObservation(request.Url!.AbsoluteUri, "Zigwheels", "Open", false, [])));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var url = Navigated.Count == 0 ? "https://zigwheels.test/" : Navigated[^1];
            return new(new BrowserOperationResult(null, new BrowserObservation(url, "Zigwheels", "Open", false, [])));
        }

        public ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return new(new BrowserCloseResult("closed"));
        }

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
