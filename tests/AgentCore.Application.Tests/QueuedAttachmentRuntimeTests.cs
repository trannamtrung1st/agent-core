using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class QueuedAttachmentRuntimeTests
{
    [Fact]
    public async Task Queued_attachment_only_binds_without_superseding()
    {
        await using var live = await LiveAsync();
        var uploaded = await live.Attachments.UploadPendingAsync(
            live.Runtime.SessionId,
            "brief.txt",
            "text/plain",
            new MemoryStream("notes"u8.ToArray()),
            false);
        var r1 = live.Runtime.ActiveResponseId;
        Assert.True(
            await live.Runtime.SubmitPersistedUserTextAsync(
                "",
                Guid.NewGuid(),
                CancellationToken.None,
                [uploaded.AttachmentId],
                UserTextBehavior.Queue) is true);
        Assert.Equal(r1, live.Runtime.ActiveResponseId);
        var queued = live.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.User);
        Assert.Equal("", queued.Text);
        Assert.Equal("brief.txt", queued.Attachments![0].DisplayName);
        Assert.Equal(AttachmentState.Bound, (await live.Attachments.GetAsync(live.Runtime.SessionId, uploaded.AttachmentId))!.State);
        live.Gate.TrySetResult();
        await live.Runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Upload_completes_during_live_response_then_queues_text_and_attachment()
    {
        await using var live = await LiveAsync();
        var r1 = live.Runtime.ActiveResponseId;
        var uploaded = await live.Attachments.UploadPendingAsync(
            live.Runtime.SessionId,
            "notes.md",
            "text/markdown",
            new MemoryStream(Encoding.UTF8.GetBytes("queued body")),
            false);
        Assert.True(
            await live.Runtime.SubmitPersistedUserTextAsync(
                "with file",
                Guid.NewGuid(),
                CancellationToken.None,
                [uploaded.AttachmentId],
                UserTextBehavior.Queue) is true);
        Assert.Equal(r1, live.Runtime.ActiveResponseId);
        var queued = live.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.User);
        Assert.Equal("with file", queued.Text);
        Assert.Equal(uploaded.AttachmentId, queued.Attachments![0].AttachmentId);
        live.Gate.TrySetResult();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitForModelRequestsAsync(live, 2, wait.Token);
        await live.Runtime.WaitUntilIdleAsync();
        Assert.Contains(
            live.Model.Requests.SelectMany(request => request.Messages),
            message => message.Role == ModelRole.User
                && message.Text.Contains("with file", StringComparison.Ordinal)
                && message.Text.Contains("notes.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_queued_users_keep_distinct_attachments_in_model_context()
    {
        await using var live = await LiveAsync();
        var first = await live.Attachments.UploadPendingAsync(
            live.Runtime.SessionId,
            "one.txt",
            "text/plain",
            new MemoryStream("alpha"u8.ToArray()),
            false);
        var second = await live.Attachments.UploadPendingAsync(
            live.Runtime.SessionId,
            "two.txt",
            "text/plain",
            new MemoryStream("beta"u8.ToArray()),
            false);
        Assert.True(
            await live.Runtime.SubmitPersistedUserTextAsync(
                "U2",
                Guid.NewGuid(),
                CancellationToken.None,
                [first.AttachmentId],
                UserTextBehavior.Queue) is true);
        Assert.True(
            await live.Runtime.SubmitPersistedUserTextAsync(
                "U3",
                Guid.NewGuid(),
                CancellationToken.None,
                [second.AttachmentId],
                UserTextBehavior.Queue) is true);
        live.Gate.TrySetResult();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitForModelRequestsAsync(live, 2, wait.Token);
        await live.Runtime.WaitUntilIdleAsync();
        var users = live.Runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).ToArray();
        Assert.Equal(["Please explain", "U2", "U3"], users.Select(entry => entry.Text).ToArray());
        Assert.Equal("one.txt", users[1].Attachments![0].DisplayName);
        Assert.Equal("two.txt", users[2].Attachments![0].DisplayName);
        var suffix = live.Model.Requests.Last(request =>
            request.Messages.Any(message => message.Role == ModelRole.User && message.Text.Contains("U2", StringComparison.Ordinal)));
        var userTurns = suffix.Messages.Where(message => message.Role == ModelRole.User).Select(message => message.Text).ToArray();
        Assert.Contains(userTurns, text => text.Contains("U2", StringComparison.Ordinal) && text.Contains("one.txt", StringComparison.Ordinal));
        Assert.Contains(userTurns, text => text.Contains("U3", StringComparison.Ordinal) && text.Contains("two.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sqlite_restart_after_persisted_queued_attachment_supplies_bindings()
    {
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        var blobRoot = Path.Combine(Path.GetTempPath(), $"agent-qattach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blobRoot);
        try
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero));
            var attachments = new SqliteAttachmentStore(harness.Factory, time, blobRoot);
            var processor = new AttachmentProcessor(attachments);
            var ids = new DeterministicIdGenerator(
                Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-00c1-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf11")]);
            var sessionId = ids.NewSessionId();
            var snapshot = new SessionSnapshot(
                1,
                sessionId,
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
            await harness.Store.SaveAsync(snapshot, 0);

            Guid attachmentId;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstOutput = new CapturingSessionOutput();
            await using (var runtime = CreateRuntime(
                             firstOutput,
                             attachments,
                             processor,
                             new ScriptedLanguageModel(ScriptedLanguageModel.LongerChunks, gate),
                             harness.Store,
                             snapshot,
                             ids,
                             time))
            {
                await runtime.AttachAsync();
                await runtime.SubmitUserTextAsync("Please explain");
                await firstOutput.WaitForAsync(item => item.Payload is TextDeltaOutput);
                var uploaded = await attachments.UploadPendingAsync(
                    runtime.SessionId,
                    "queued.md",
                    "text/markdown",
                    new MemoryStream(Encoding.UTF8.GetBytes("persisted queued")),
                    false);
                attachmentId = uploaded.AttachmentId;
                Assert.True(
                    await runtime.SubmitPersistedUserTextAsync(
                        "queued with file",
                        Guid.NewGuid(),
                        CancellationToken.None,
                        [uploaded.AttachmentId],
                        UserTextBehavior.Queue) is true);
                await WaitForBoundAsync(attachments, runtime.SessionId, uploaded.AttachmentId);
                await runtime.DetachAsync();
                await WaitForStatusAsync(harness.Store, sessionId, SessionStatus.Paused);
            }

            var reloaded = await harness.Store.LoadAsync(sessionId);
            Assert.NotNull(reloaded);
            var queued = reloaded!.Entries.Last(entry => entry.Role == ConversationRole.User);
            Assert.Equal("queued with file", queued.Text);
            Assert.Equal(attachmentId, queued.Attachments![0].AttachmentId);

            var reopenOutput = new CapturingSessionOutput();
            var model = new RecordingLanguageModel(new ScriptedLanguageModel());
            var resumed = await PausedSessionReopen.ReopenAsync(harness.Store, reloaded, time);
            await using var reopened = CreateRuntime(
                reopenOutput,
                attachments,
                processor,
                model,
                harness.Store,
                resumed,
                ids,
                time);
            Assert.True(await reopened.AttachAsync());
            using var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await reopenOutput.WaitForAsync(item => item.Payload is ResponseCompletedOutput, recovered.Token);
            await reopened.WaitUntilIdleAsync();
            Assert.Contains(
                model.Requests.SelectMany(request => request.Messages),
                message => message.Role == ModelRole.User
                    && message.Text.Contains("queued with file", StringComparison.Ordinal)
                    && message.Text.Contains("queued.md", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(blobRoot, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<LiveHarness> LiveAsync()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new RecordingLanguageModel(new ScriptedLanguageModel(ScriptedLanguageModel.LongerChunks, gate));
        var output = new CapturingSessionOutput();
        var runtime = CreateRuntime(output, attachments, processor, model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Please explain");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        return new LiveHarness(runtime, output, attachments, model, gate);
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null,
        IIdGenerator? ids = null,
        FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        ids ??= new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-00c0-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf10")]);
        store ??= new InMemoryMemoryStore();
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
            null,
            time.GetUtcNow(),
            time.GetUtcNow());
        if (snapshot.Revision == 1 && snapshot.Entries.Count == 0)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments,
            processor: processor);
    }

    private static async Task WaitForModelRequestsAsync(LiveHarness live, int minimum, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 750; attempt++)
        {
            if (live.Model.Requests.Count >= minimum)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        Assert.True(
            live.Model.Requests.Count >= minimum,
            $"Expected at least {minimum} model requests, observed {live.Model.Requests.Count}.");
    }

    private static async Task WaitForBoundAsync(IAttachmentStore attachments, Guid sessionId, Guid attachmentId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var record = await attachments.GetAsync(sessionId, attachmentId);
            if (record?.State == AttachmentState.Bound)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Equal(AttachmentState.Bound, (await attachments.GetAsync(sessionId, attachmentId))?.State);
    }

    private static async Task WaitForStatusAsync(IMemoryStore store, Guid sessionId, SessionStatus status)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = await store.LoadAsync(sessionId);
            if (snapshot?.Status == status)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Equal(status, (await store.LoadAsync(sessionId))?.Status);
    }

    private sealed record LiveHarness(
        SessionRuntime Runtime,
        CapturingSessionOutput Output,
        IAttachmentStore Attachments,
        RecordingLanguageModel Model,
        TaskCompletionSource Gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
