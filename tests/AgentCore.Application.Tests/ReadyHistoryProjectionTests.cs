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

public sealed class ReadyHistoryProjectionTests
{
    [Fact]
    public async Task Attach_ready_uses_durable_newest_page_when_runtime_restore_window_is_bounded()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var now = time.GetUtcNow();
        var allEntries = Enumerable.Range(1, 500)
            .Select(sequence => Entry(sequence, now))
            .ToArray();
        var durable = new SessionSnapshot(
            1,
            sessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            allEntries,
            string.Empty,
            500,
            null,
            null,
            now,
            now);
        await store.SaveAsync(durable, 0);

        var windowed = HistoryRestoreWindow.Select(allEntries);
        Assert.True(windowed.Count < 50);

        var runtimeSnapshot = durable with
        {
            Entries = windowed,
            Status = SessionStatus.Created
        };

        await using var runtime = Create(output, store, time, runtimeSnapshot);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();

        var ready = Assert.IsType<ReadyOutput>(output.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal(50, ready.Ready.History.Count);
        Assert.Equal(451, ready.Ready.History[0].Sequence);
        Assert.Equal(500, ready.Ready.History[^1].Sequence);
        Assert.True(ready.Ready.HasOlderHistory);
        Assert.Equal(451, ready.Ready.HistoryBeforeSequence);
    }

    private static ConversationEntry Entry(long sequence, DateTimeOffset now)
    {
        var entryId = Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}");
        var isUser = sequence % 2 == 1;
        var text = $"History seed {sequence}";
        return new ConversationEntry(
            entryId,
            sequence,
            isUser ? entryId : null,
            isUser ? ConversationRole.User : ConversationRole.Assistant,
            text,
            isUser ? null : Guid.Parse($"019944af-0001-7000-8000-{sequence:D12}"),
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            now);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        InMemoryMemoryStore store,
        FakeTimeProvider time,
        SessionSnapshot snapshot)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [snapshot.SessionId]);
        return new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
    }
}
