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
    public async Task Approval_flow_executes_once_after_explicit_approve()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approvalEvent.Payload!;
        Assert.Equal(ToolCatalog.DemoSensitiveAction, requested.ToolName);
        var pendingOnReady = runtime.Runtime.BuildPublicPendingApproval();
        Assert.NotNull(pendingOnReady);
        Assert.Equal(requested.ApprovalId, pendingOnReady!.ApprovalId);
        Assert.Equal(requested.Summary, pendingOnReady.Summary);
        Assert.Contains(
            output.Items,
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.WaitingExternal
                && progress.State == ResponseProgressState.Started);

        Assert.Equal(ResponseApprovalResult.Accepted, await runtime.Runtime.RespondApprovalAsync(
            runtime.Runtime.ActiveResponseId!.Value,
            requested.ApprovalId,
            ToolApprovalDecision.Approve));

        await runtime.Runtime.WaitUntilIdleAsync();
        var assistant = runtime.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("completed after approval", assistant.Text, StringComparison.OrdinalIgnoreCase);

        var completedWaiting = output.Items
            .Select(item => item.Payload)
            .OfType<ResponseProgressOutput>()
            .Count(progress =>
                progress.Kind == ResponseProgressKind.WaitingExternal
                && progress.State == ResponseProgressState.Completed);
        Assert.Equal(1, completedWaiting);

        var duplicate = await runtime.Runtime.RespondApprovalAsync(
            runtime.Runtime.ActiveResponseId ?? requested.ApprovalId,
            requested.ApprovalId,
            ToolApprovalDecision.Approve);
        Assert.True(duplicate is ResponseApprovalResult.Stale or ResponseApprovalResult.Unknown or ResponseApprovalResult.Idempotent);
    }

    [Fact]
    public async Task Stale_approval_id_on_respond_returns_stale()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approvalEvent.Payload!;
        var responseId = runtime.Runtime.ActiveResponseId!.Value;

        Assert.Equal(
            ResponseApprovalResult.Stale,
            await runtime.Runtime.RespondApprovalAsync(
                responseId,
                Guid.NewGuid(),
                ToolApprovalDecision.Approve));
    }

    [Fact]
    public async Task Stale_response_id_on_respond_returns_stale()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approvalEvent.Payload!;

        Assert.Equal(
            ResponseApprovalResult.Stale,
            await runtime.Runtime.RespondApprovalAsync(
                Guid.NewGuid(),
                requested.ApprovalId,
                ToolApprovalDecision.Approve));
    }

    [Fact]
    public async Task Approval_expires_without_executing_sensitive_action()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = await CreateGeneralV3AtTimeAsync(output, time);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        time.Advance(ToolApprovalLimits.Lifetime + TimeSpan.FromSeconds(1));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant
                && entry.Text.Contains("completed after approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deactivate_during_approval_prevents_sensitive_execution()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        Assert.True(await runtime.Runtime.RequestDeactivateAsync());
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant
                && entry.Text.Contains("completed after approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Interrupt_during_approval_prevents_sensitive_execution()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("stop", behavior: UserTextBehavior.Interrupt));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant
                && entry.Text.Contains("completed after approval", StringComparison.OrdinalIgnoreCase));
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
    public async Task Detach_during_approval_prevents_sensitive_execution()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        await runtime.Runtime.DetachAsync();
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant
                && entry.Text.Contains("completed after approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reject_prevents_sensitive_execution()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        await using var runtime = await CreateGeneralV3Async(output);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Need sensitive approval now."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approvalEvent.Payload!;
        Assert.Equal(
            ResponseApprovalResult.Accepted,
            await runtime.Runtime.RespondApprovalAsync(
                runtime.Runtime.ActiveResponseId!.Value,
                requested.ApprovalId,
                ToolApprovalDecision.Reject));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant
                && entry.Text.Contains("completed after approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Approval_wait_does_not_consume_per_tool_or_overall_execution_budget()
    {
        DemoSensitiveActionStore.Reset();
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = await CreateGeneralV3AtTimeAsync(output, time);
        await runtime.Runtime.AttachAsync();

        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Please run sensitive approval for the demo."));
        var approvalEvent = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approvalEvent.Payload!;
        time.Advance(ToolLimits.PerTool + TimeSpan.FromSeconds(1));
        time.Advance(ToolLimits.Overall);
        Assert.Equal(
            ResponseApprovalResult.Accepted,
            await runtime.Runtime.RespondApprovalAsync(
                runtime.Runtime.ActiveResponseId!.Value,
                requested.ApprovalId,
                ToolApprovalDecision.Approve));
        await runtime.Runtime.WaitUntilIdleAsync();
        var assistant = runtime.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("completed after approval", assistant.Text, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<Harness> CreateGeneralV3Async(CapturingSessionOutput output) =>
        await CreateGeneralV3AtTimeAsync(output, new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)));

    private static async Task<Harness> CreateGeneralV3AtTimeAsync(CapturingSessionOutput output, FakeTimeProvider time)
    {
        var definition = await LoadGeneralV3();
        var tools = new SessionToolExecutor();
        var model = new ScriptedLanguageModel();
        var runtime = CreateRuntime(output, definition, model, tools, time);
        return new Harness(runtime);
    }

    private static async Task<AgentDefinition> LoadGeneralV3()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", 3))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("agents directory not found.");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        AgentDefinition definition,
        ILanguageModel model,
        SessionToolExecutor tools,
        FakeTimeProvider? time = null)
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
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: tools);
    }

    private static AgentDefinition Definition(string id, int version, IReadOnlyList<string> tools) =>
        new(
            1,
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
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
