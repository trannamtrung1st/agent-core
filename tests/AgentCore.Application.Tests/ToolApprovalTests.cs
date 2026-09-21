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

        var duplicate = await runtime.Runtime.RespondApprovalAsync(
            runtime.Runtime.ActiveResponseId ?? requested.ApprovalId,
            requested.ApprovalId,
            ToolApprovalDecision.Approve);
        Assert.True(duplicate is ResponseApprovalResult.Stale or ResponseApprovalResult.Unknown or ResponseApprovalResult.Idempotent);
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

    private static async Task<Harness> CreateGeneralV3Async(CapturingSessionOutput output)
    {
        var definition = await LoadGeneralV3();
        var tools = new SessionToolExecutor();
        var model = new ScriptedLanguageModel();
        var runtime = CreateRuntime(output, definition, model, tools);
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
        SessionToolExecutor tools)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
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
}
