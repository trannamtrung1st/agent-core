using AgentCore.Application.Agents;
using AgentCore.Application.Events;
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

public sealed class AcceptedTurnDetachDurabilityTests
{
    [Fact]
    public async Task Detach_after_ack_before_response_still_executes()
    {
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await WaitUntilAsync(() => runtime.HasAcceptedConversationWorkAsync());

        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await runtime.WaitUntilAcceptedConversationWorkSettledAsync();

        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
    }

    [Fact]
    public async Task Idle_detach_still_pauses_disconnected()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(
            output,
            new ScriptedLanguageModel(),
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("disconnected", runtime.Snapshot.PauseReason);
        Assert.False(await runtime.HasAcceptedConversationWorkAsync());
    }

    [Fact]
    public async Task Cancel_after_transport_detach_still_interrupts()
    {
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await WaitForActiveResponseAsync(runtime);
        var responseId = runtime.ActiveResponseId!.Value;

        await runtime.DetachAsync();
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(responseId));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.Contains(
            output.Items,
            item => item.ResponseId == responseId
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason == "userStop");
        await runtime.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail("Condition was not met before timeout.");
    }

    private static async Task WaitForActiveResponseAsync(SessionRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (runtime.ActiveResponseId is not null)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Active response was not established.");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        IMemoryStore? store = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var memory = store ?? new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
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
        memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            brain,
            memory,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier());
    }

}

file sealed class DetachHoldingLanguageModel : ILanguageModel
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ModelTextDelta("T1");
        await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }
}
