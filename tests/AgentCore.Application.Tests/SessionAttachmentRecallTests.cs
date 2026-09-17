using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionAttachmentRecallTests
{
    [Fact]
    public async Task Later_turn_includes_session_attachment_manifest_and_entry_refs()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments, processor, model, brain);
        await runtime.AttachAsync();
        var body = "Alpha beta gamma delta";
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "proposal.md",
            "text/markdown",
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Review this file.", attachmentIds: [uploaded.AttachmentId]));
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);

        var firstEntry = runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.User);
        Assert.NotNull(firstEntry.Attachments);
        Assert.Equal("proposal.md", firstEntry.Attachments![0].DisplayName);

        Assert.True(await runtime.SubmitUserTextAsync("Count the words in that file."));
        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completedBefore = output.Terminals.Count;
        await output.WaitForAsync(
            item => item.Payload is ResponseCompletedOutput && output.Terminals.Count == completedBefore + 1,
            second.Token);
        await runtime.WaitUntilIdleAsync();

        Assert.True(brain.Contexts.Count >= 2);
        var followUpContext = brain.Contexts[^1];
        Assert.Equal("Count the words in that file.", followUpContext.Trigger.Text);
        Assert.NotNull(followUpContext.SessionAttachments);
        Assert.Contains(
            followUpContext.SessionAttachments!,
            item => item.AttachmentId == uploaded.AttachmentId && item.DisplayName == "proposal.md");
        var manifest = new PromptContextBuilder().BuildSections(followUpContext).AttachmentManifestSystem;
        Assert.Contains("proposal.md", manifest, StringComparison.Ordinal);
        Assert.Contains(uploaded.AttachmentId.ToString("D"), manifest, StringComparison.OrdinalIgnoreCase);

        var request = model.LastRequest!;
        Assert.Contains(
            request.Messages,
            message => message.Role == ModelRole.System && message.Text.Contains("Files available in this session", StringComparison.Ordinal));
        Assert.Contains(
            request.Messages,
            message => message.Role == ModelRole.User
                && message.Text.Contains("Review this file.", StringComparison.Ordinal)
                && message.Text.Contains("proposal.md", StringComparison.Ordinal)
                && message.Text.Contains(uploaded.AttachmentId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            request.Messages,
            message => message.Role == ModelRole.User && message.Text.Contains("Count the words in that file.", StringComparison.Ordinal));
        Assert.Contains(request.Tools ?? [], tool => tool.Name == ToolCatalog.AttachmentsRead);
    }

    [Fact]
    public void BuildAttachmentManifestSystem_is_empty_without_session_attachments()
    {
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"));
        Assert.Equal(string.Empty, new PromptContextBuilder().BuildSections(context).AttachmentManifestSystem);
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        IAgentBrain brain)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0013-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b843")]);
        var store = new InMemoryMemoryStore();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Support,
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
            model,
            brain,
            store,
            output,
            ids,
            time,
            NullLogger.Instance,
            attachments: attachments,
            processor: processor);
    }
}
