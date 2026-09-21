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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class HistoricalImageSyntheticContinuationTests
{
    [Fact]
    public async Task Scripted_vision_tools_flow_completes_historical_image_reread()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var tools = new SessionToolExecutor(attachments: attachments, processor: processor);
        var model = new VisionToolsLanguageModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = CreateRuntime(output, store, attachments, processor, model, tools);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Warm up", attachmentIds: [uploaded.AttachmentId]));
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);

        Assert.True(await runtime.SubmitUserTextAsync(
            $"{ScriptedLanguageModel.HistoricalImageRereadMarker} Please inspect the earlier image."));
        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await output.WaitForAsync(
            item => item.Payload is ResponseCompletedOutput && output.Terminals.Count >= 2,
            second.Token);
        await runtime.WaitUntilIdleAsync();

        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains(ScriptedLanguageModel.HistoricalImageRereadAnswer, assistant.Text, StringComparison.Ordinal);

        var persisted = (await store.LoadAsync(runtime.SessionId))!;
        var combined = string.Join('\n', persisted.Entries.Select(entry => entry.Text));
        Assert.DoesNotContain("Treat this as tool data", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(PngBytes()), combined, StringComparison.Ordinal);
        Assert.All(persisted.Entries, entry => Assert.True(entry.Role is ConversationRole.User or ConversationRole.Assistant));
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        InMemoryMemoryStore store,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        SessionToolExecutor tools)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00a1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var now = time.GetUtcNow();
        var definition = Support() with
        {
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.AttachmentsRead])
        };
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
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments,
            processor: processor,
            tools: tools);
    }

    private static AgentDefinition Support() => new(
        1,
        "customer-support",
        1,
        new AgentIdentity("Sam", "Support", "Help.", "Warm"),
        ["Help"],
        "Help.",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 10000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences("primary-llm", null, null),
        new Dictionary<string, string>(),
        new RoleEnvironment(ToolAllowlist: [ToolCatalog.AttachmentsRead]));

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private sealed class VisionToolsLanguageModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = inner.Capabilities with { Vision = true, Tools = true };

        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(request, cancellationToken);
    }
}
