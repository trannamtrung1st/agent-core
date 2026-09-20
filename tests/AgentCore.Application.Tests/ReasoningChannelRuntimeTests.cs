using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ReasoningChannelRuntimeTests
{
    [Fact]
    public async Task Reasoning_deltas_do_not_become_assistant_display_text()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var definition = SampleDefinitions.Examiner;
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1, ids.NewSessionId(), 1, definition, SessionMode.Text, null,
            SessionStatus.Created, [], string.Empty, 0, null, null, now, now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new FixedEventLanguageModel(
            [
                new ModelReasoningDelta("internal planning must stay private"),
                new ModelTextDelta("Public answer only."),
                new ModelCompleted(ModelStopReason.Completed)
            ]),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Public answer only.", assistant.Text);
        Assert.DoesNotContain("internal planning", assistant.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(output.TextDeltas, delta => delta.Text.Contains("internal planning", StringComparison.Ordinal));
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseProgressOutput);
    }

    private sealed class FixedEventLanguageModel(IReadOnlyList<ModelGenerationEvent> events) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = request;
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }
}
