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

public sealed class LiveSessionRenameTests
{
    [Fact]
    public async Task Rename_while_attached_advances_revision_and_allows_followup_turn()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = CreateRuntime(output, store);
        await runtime.AttachAsync();
        var revisionAtAttach = runtime.Snapshot.Revision;

        Assert.True(await runtime.RequestRenameAsync("Planning notes"));
        Assert.Equal("Planning notes", runtime.Snapshot.Title);
        Assert.True(runtime.Snapshot.Revision > revisionAtAttach);

        Assert.True(await runtime.SubmitUserTextAsync("Hello after rename"));
        await runtime.WaitUntilIdleAsync();

        var assistant = runtime.Snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
        Assert.NotNull(assistant);
        Assert.Equal(EntryStatus.Completed, assistant!.Status);
        var loaded = await store.LoadAsync(runtime.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(runtime.Snapshot.Revision, loaded.Revision);
    }

    private static SessionRuntime CreateRuntime(ISessionOutput output, IMemoryStore store)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0010-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
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
            time.GetUtcNow(),
            time.GetUtcNow());
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
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
