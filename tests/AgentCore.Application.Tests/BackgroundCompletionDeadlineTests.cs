using System.Runtime.CompilerServices;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed partial class AgentRunDurabilityTests
{
    [Theory]
    [InlineData(150, AgentRunStatus.Completed)]
    [InlineData(181, AgentRunStatus.Failed)]
    public async Task Background_work_cutoff_preserves_completion_tool_but_hard_deadline_fails(int elapsedSeconds, AgentRunStatus expected)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T00:00:00Z"));
        var memory = new InMemoryMemoryStore();
        var runs = new RuntimeAgentRunStore();
        runs.Bind(memory);
        var runId = Guid.NewGuid(); var now = time.GetUtcNow(); var sessionId = Guid.NewGuid();
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User,
            "Check the background objective", null, EntryStatus.Completed, SessionMode.Text, 0, 30, now);
        var snapshot = RuntimeAgentRunStore.WithPins(new SessionSnapshot(1, sessionId, 1, SampleDefinitions.Examiner,
            SessionMode.Text, null, SessionStatus.Created, [input], "", 0, null, null, now, now,
            AgentInstanceId: Guid.NewGuid(), ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.Host, null),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces: SessionSurface.BackgroundWork));
        var activation = Activation.Start(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [input.EntryId],
            input.SourceEventId, null, null, null, "manual:test", now);
        var run = AgentRun.Create(runId, new(snapshot.AgentInstanceId, snapshot.ProfileId!.Value),
            new(activation, snapshot.Definition.Id, snapshot.Definition.Version, snapshot.PinnedPersona!, Guid.NewGuid(), AgentRunOutputContract.BackgroundOutcome,
                executionBudget: new(ExecutionBudgetClass.Standard, ExecutionBudgetProfile.For(ExecutionBudgetClass.Standard, ExecutionBudgetPreset.Standard), "system"),
                configuration: new(snapshot.Definition, 0, snapshot.PinnedPersonaRevision ?? 0, [])),
            new("synthetic-offline/scripted", "primary-llm", "scripted", null), 3, now);
        await runs.AdmitAsync(snapshot, 0, run);
        await runs.ApplyAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var model = new DeadlineBackgroundModel();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(runId, headless: true));
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = (await runs.GetAsync(run.Owner, runId))!;
        Assert.Equal(expected, terminal.Status);
        Assert.Equal(1, terminal.AttemptCount);
        if (expected == AgentRunStatus.Failed)
        {
            Assert.Equal("run-deadline", terminal.Failure!.Code);
            Assert.Null(terminal.Result);
            Assert.Single(model.Requests);
        }
        else
        {
            Assert.Equal(2, model.Requests.Count);
            var recovery = model.Requests.Last();
            Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(recovery.Tools!).Name);
            Assert.Equal(ModelToolChoice.Named, recovery.ToolChoice);
            Assert.Null(recovery.ResponseContract);
            Assert.Equal(AgentRunOutcomeKind.NeedsAttention, terminal.Result!.OutcomeKind);
        }
    }

    private sealed class DeadlineBackgroundModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            yield return new ModelToolCallEvent(new("finish", ToolCatalog.WorkComplete,
                "{\"summary\":\"The work cutoff prevented verification.\",\"attentionRequired\":true,\"outcome\":\"NeedsAttention\"}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
