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

public sealed class DeliveryReceiptTests
{
    [Fact]
    public async Task Generated_text_is_not_received_until_client_receipt()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = Create(output, store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Hello from synthetic.", assistant.Text);
        Assert.Equal(0, assistant.ReceivedTextEndExclusive);
        Assert.Equal(0, assistant.HeardTextEndExclusive);

        var accepted = await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length);
        Assert.True(accepted);
        await runtime.WaitUntilMailboxDrainedAsync();
        assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(assistant.Text.Length, assistant.ReceivedTextEndExclusive);
        Assert.Equal(0, assistant.HeardTextEndExclusive);
        Assert.False(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length + 1));
        Assert.False(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length - 1));
        await runtime.WaitUntilMailboxDrainedAsync();
        assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(assistant.Text.Length, assistant.ReceivedTextEndExclusive);
    }

    [Fact]
    public async Task Duplicate_source_event_does_not_create_a_second_user_turn()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var source = Guid.Parse("019944af-0000-7000-8000-000000000321");
        await using var first = Create(output, store);
        await first.AttachAsync();
        Assert.True(await first.SubmitUserTextAsync("Hello", source));
        await first.WaitUntilIdleAsync();
        await first.DetachAsync();
        await first.WaitUntilMailboxDrainedAsync();

        var restored = (await store.LoadAsync(first.SessionId))!;
        var secondOutput = new CapturingSessionOutput();
        await using var runtime = Create(secondOutput, store, restored);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Hello", source));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
    }

    private static SessionRuntime Create(ISessionOutput output, InMemoryMemoryStore store, SessionSnapshot? snapshot = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
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
            LocalUserProfile.Id,
            now,
            now);
        if (store.LoadAsync(snapshot.SessionId).AsTask().GetAwaiter().GetResult() is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

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
