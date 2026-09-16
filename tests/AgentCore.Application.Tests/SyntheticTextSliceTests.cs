using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SyntheticTextSliceTests
{
    [Fact]
    public async Task SubmitUserText_emits_ordered_deltas_and_one_terminal()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel());

        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();

        var deltas = output.TextDeltas;
        Assert.Equal(["Hello", " from ", "synthetic."], deltas.Select(delta => delta.Text).ToArray());
        Assert.Equal(0, deltas[0].TextStart);
        Assert.Equal(5, deltas[1].TextStart);
        Assert.Equal(11, deltas[2].TextStart);
        Assert.Single(output.Terminals);
        Assert.False(output.Terminals[0].Failed);
        Assert.Contains(output.Items, item => item.Payload is TextCompletedOutput);
        Assert.Equal(1, output.Items.Count(item => item.Payload is ResponseCompletedOutput));
        Assert.Equal("Hello", runtime.Snapshot.Title);
    }

    [Fact]
    public async Task Cancel_in_flight_generation_hides_late_chunks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(
            ["AAA", "BBB", "CCC"],
            releaseAfterFirstChunk: release);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, model);

        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "AAA");

        await runtime.CancelActiveResponseAsync();
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(output.TextDeltas, delta => delta.Text is "BBB" or "CCC");
        Assert.Single(output.Terminals);
        Assert.True(output.Terminals[0].Failed);
        Assert.DoesNotContain(output.Items, item => item.Payload is TextCompletedOutput);
    }

    private static SessionRuntime CreateRuntime(ISessionOutput output, ILanguageModel model)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var definition = SampleDefinitions.Examiner;
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
            NullLogger<SessionRuntime>.Instance);
    }
}
