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

public sealed class AttachmentBindTests
{
    [Fact]
    public async Task Attachments_only_first_message_binds_after_persist_and_sets_filename_title()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "brief.txt",
            "text/plain",
            new MemoryStream("notes"u8.ToArray()),
            false);

        var rejected = await Assert.ThrowsAsync<AgentCoreException>(() => runtime.SubmitUserTextAsync("   "));
        Assert.Equal("ValidationError", rejected.Code);

        Assert.True(await runtime.SubmitUserTextAsync("", attachmentIds: [uploaded.AttachmentId]));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal("brief.txt", runtime.Snapshot.Title);
        var bound = await attachments.GetAsync(runtime.SessionId, uploaded.AttachmentId);
        Assert.Equal(AttachmentState.Bound, bound!.State);
        Assert.Equal(uploaded.Sha256Hex, bound.Sha256Hex);
        Assert.Equal(runtime.Snapshot.Entries[0].EntryId, bound.EntryId);
    }

    [Fact]
    public async Task Rename_overrides_generated_title_and_text_only_does_not_bind_staged_files()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var store = new InMemoryMemoryStore();
        var manager = new SessionManager(
            new TestDefinitions(SampleDefinitions.Examiner),
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0004-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]),
            TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            attachments);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var renamed = await manager.RenameAsync(created.SessionId, "Planning notes");
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments, store, renamed);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "ignored.txt",
            "text/plain",
            new MemoryStream("x"u8.ToArray()),
            false);
        await attachments.StageForNextTurnAsync(runtime.SessionId, [uploaded.AttachmentId]);
        await runtime.SubmitUserTextAsync("Hello without files");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal("Planning notes", runtime.Snapshot.Title);
        Assert.Equal(AttachmentState.Pending, (await attachments.GetAsync(runtime.SessionId, uploaded.AttachmentId))!.State);
    }

    [Fact]
    public async Task Durable_delete_removes_attachment_blobs()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var store = new InMemoryMemoryStore();
        var manager = new SessionManager(
            new TestDefinitions(SampleDefinitions.Examiner),
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0005-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b801")]),
            TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            attachments);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var uploaded = await attachments.UploadPendingAsync(
            created.SessionId,
            "a.txt",
            "text/plain",
            new MemoryStream("x"u8.ToArray()),
            false);
        await manager.DurablyDeleteAsync(created.SessionId, created.Revision);
        Assert.Null(await attachments.GetAsync(created.SessionId, uploaded.AttachmentId));
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IMemoryStore? store = null,
        SessionSnapshot? existing = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0006-7000-8000-{index:D12}")),
            [existing?.SessionId ?? Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        store ??= new InMemoryMemoryStore();
        var snapshot = existing ?? new SessionSnapshot(
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
        if (existing is null)
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
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments);
    }

    private sealed class TestDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
