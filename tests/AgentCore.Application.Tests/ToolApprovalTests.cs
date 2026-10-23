using System.Text.Json;
using System.Reflection;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ToolApprovalTests
{


    [Fact(Timeout = 15000)]
    public async Task Unowned_review_requirement_finishes_without_an_inline_approval_or_sensitive_effect()
    {
        var output = new CapturingSessionOutput();
        var tracking = new TrackingEmailProvider();
        var model = new EmailSendOnceLanguageModel();
        var tools = new SessionToolExecutor(emailProvider: tracking, configurationGate: ToolConfigurationGates.AllowAll);
        await using var runtime = new Harness(CreateRuntime(output, Definition("review-without-tenant", 1, [ToolCatalog.EmailSend]), model, tools));
        await runtime.Runtime.AttachAsync();
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Send the draft"));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.Equal(0, tracking.GetDraftCount);
        Assert.Contains(model.Requests.SelectMany(request => request.Messages), message => message.Role == ModelRole.Tool && message.Text.Contains("tenant_context_required", StringComparison.Ordinal));
        Assert.DoesNotContain(output.Items, item => item.Payload?.GetType().Name == "ApprovalRequestedOutput");
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseProgressOutput { Kind: ResponseProgressKind.WaitingExternal });
        Assert.Null(typeof(SessionRuntime).GetMethod("RespondApprovalAsync"));
        Assert.Null(typeof(SessionRuntime).GetMethod("ControlAgentRunAsync"));
        Assert.Null(typeof(OutputPayload).Assembly.GetType("AgentCore.Application.Events.ApprovalRequestedOutput"));
        Assert.Null(typeof(OutputPayload).Assembly.GetType("AgentCore.Application.Events.ApprovalResponseReceived"));
        Assert.Contains(runtime.Runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
    }

    [Fact]
    public void Sensitive_tools_require_approval_without_grant()
    {
        var definition = Definition(
            "general-assistant",
            3,
            [ToolCatalog.DemoSensitiveAction]);
        var gate = ToolConfigurationGates.AllowAll;
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.DemoSensitiveAction, gate));
        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.DemoSensitiveAction,
            "abc",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(definition, ToolCatalog.DemoSensitiveAction, gate, grant));
    }


    [Fact]
    public async Task Duplicate_demo_sensitive_execution_reports_duplicate_error()
    {
        DemoSensitiveActionStore.Reset();
        var tools = new SessionToolExecutor();
        var definition = Definition("general-assistant", 3, [ToolCatalog.DemoSensitiveAction]);
        var sessionId = Guid.NewGuid();
        var args = JsonDocument.Parse("""{"label":"once"}""").RootElement;
        var grant = new ToolApprovalGrant(
            Guid.NewGuid(),
            ToolCatalog.DemoSensitiveAction,
            ToolActionHash.Compute(ToolCatalog.DemoSensitiveAction, args),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var first = await tools.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c1", ToolCatalog.DemoSensitiveAction, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("\"status\":\"completed\"", first.Text, StringComparison.OrdinalIgnoreCase);
        var second = await tools.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("c2", ToolCatalog.DemoSensitiveAction, args.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant);
        Assert.Contains("duplicate", second.Text, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task Denied_email_send_does_not_read_the_provider_draft()
    {
        var tracking = new TrackingEmailProvider();
        var output = new CapturingSessionOutput();
        var tools = new SessionToolExecutor(
            emailProvider: tracking,
            configurationGate: ToolConfigurationGates.AllowAll);
        var definition = Definition("examiner", 1, []);
        var model = new EmailSendOnceLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = new Harness(CreateRuntime(output, definition, model, tools, time));
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("send the draft"));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.Equal(0, tracking.GetDraftCount);
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        AgentDefinition definition,
        ILanguageModel model,
        SessionToolExecutor tools,
        FakeTimeProvider? time = null, IAgentRunStore? agentRuns = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-00b1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var store = new InMemoryMemoryStore();
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
            now, AgentInstanceId: Guid.NewGuid(), ModelSelection: agentRuns is null ? null : new SessionModelSelection(
                "synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.SystemDefault, null));
        if (agentRuns is not null)
        {
            snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        }
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: tools, agentRuns: agentRuns);
    }

    private static AgentDefinition Definition(string id, int version, IReadOnlyList<string> tools) =>
        new(
            2,
            id,
            version,
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

    private sealed record Harness(SessionRuntime Runtime) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class EmailSendOnceLanguageModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("Noted.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return new ModelToolCallEvent(
                new ModelToolCall("call-email-send", ToolCatalog.EmailSend, """{"draftId":"draft-denied"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class TrackingEmailProvider : IEmailProvider
    {
        private readonly SyntheticEmailProvider _inner = new();

        public int GetDraftCount { get; private set; }

        public bool IsAvailable => _inner.IsAvailable;

        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(request, cancellationToken);

        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(request, cancellationToken);

        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.CreateDraftAsync(request, cancellationToken);

        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
        {
            GetDraftCount++;
            return _inner.GetDraftAsync(draftId, cancellationToken);
        }

        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default) =>
            _inner.SendDraftAsync(request, cancellationToken);
    }
}
