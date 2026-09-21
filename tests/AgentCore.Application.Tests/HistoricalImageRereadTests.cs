using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class HistoricalImageRereadTests
{
    [Fact]
    public async Task Attachments_read_returns_sanitized_image_part_for_historical_png()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        var result = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        var part = Assert.IsType<ModelImageContent>(Assert.Single(result.Parts!));
        Assert.Equal("image/png", part.ContentType);
        Assert.Equal("photo.png", part.FileName);
        Assert.DoesNotContain(Convert.ToBase64String(part.Bytes), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attachments_read_normalizes_webp_to_png_bytes_with_truthful_mime()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        using var image = new Image<Rgba32>(3, 3, new Rgba32(1, 2, 3));
        using var webp = new MemoryStream();
        image.SaveAsWebp(webp);
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "shot.webp",
            "image/webp",
            new MemoryStream(webp.ToArray()),
            false);
        var result = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        var part = Assert.IsType<ModelImageContent>(Assert.Single(result.Parts!));
        Assert.Equal("image/png", part.ContentType);
    }

    [Fact]
    public async Task Cross_session_attachment_id_is_not_found()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            owner,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        var result = await executor.ExecuteAsync(
            Support(),
            other,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("notFound", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Parts);
    }

    [Fact]
    public void Tool_result_admission_strips_image_parts_without_vision()
    {
        var image = new ModelImageContent("image/png", [1, 2, 3], "x.png");
        var original = new ToolExecutionResult("""{"kind":"image"}""", [image]);
        var admitted = ToolResultAdmission.AdmitForModel(
            new NonVisionLanguageModel(),
            original);
        Assert.Null(admitted.Parts);
        Assert.Contains("vision_required", admitted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_loop_replaces_image_parts_when_model_lacks_vision()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var tools = new SessionToolExecutor(attachments: attachments, processor: processor);
        var model = new HistoricalImageToolLoopModel();
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments, processor, model, tools);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("First", attachmentIds: [uploaded.AttachmentId]));
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);
        Assert.True(await runtime.SubmitUserTextAsync("Reread the image please."));
        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(
            item => item.Payload is ResponseCompletedOutput && model.SawVisionRequiredToolResult,
            second.Token);
        await runtime.WaitUntilIdleAsync();

        var toolMessage = model.LastToolMessage!;
        Assert.Contains("vision_required", toolMessage.Text, StringComparison.Ordinal);
        Assert.Null(toolMessage.Parts);
    }

    [Fact]
    public async Task Attachments_read_returns_sanitized_image_part_for_historical_jpeg()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "photo.jpg",
            "image/jpeg",
            new MemoryStream(JpegBytes()),
            false);
        var result = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        var part = Assert.IsType<ModelImageContent>(Assert.Single(result.Parts!));
        Assert.Equal("image/jpeg", part.ContentType);
        Assert.True(part.Bytes.Length >= 3 && part.Bytes[0] == 0xFF && part.Bytes[1] == 0xD8);
    }

    [Fact]
    public async Task Attachments_read_normalizes_gif_to_png_bytes_with_truthful_mime()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "anim.gif",
            "image/gif",
            new MemoryStream(GifBytes()),
            false);
        var result = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        var part = Assert.IsType<ModelImageContent>(Assert.Single(result.Parts!));
        Assert.Equal("image/png", part.ContentType);
        Assert.Contains("\"contentType\":\"image/png\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_attachment_id_returns_not_found()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var executor = new SessionToolExecutor(attachments: attachments, processor: new AttachmentProcessor(attachments));
        var result = await executor.ExecuteAsync(
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{Guid.NewGuid():D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("notFound", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Parts);
    }

    [Fact]
    public async Task Processor_failure_returns_attachment_processing_failed()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        var withoutProcessor = new SessionToolExecutor(attachments: attachments);
        var missingProcessor = await withoutProcessor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("attachment_processing_failed", missingProcessor.Text, StringComparison.Ordinal);
        Assert.Null(missingProcessor.Parts);

        var emptyProcessor = new SessionToolExecutor(attachments: attachments, processor: new EmptyImageProcessor());
        var empty = await emptyProcessor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c2", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("attachment_processing_failed", empty.Text, StringComparison.Ordinal);
        Assert.Null(empty.Parts);
    }

    [Fact]
    public void Vision_capable_tool_less_model_does_not_offer_attachments_read_for_images()
    {
        var attachmentId = Guid.Parse("019944af-0014-7000-8000-000000000099");
        var context = new AgentContext(
            Support(),
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Describe the photo."),
            SessionAttachments:
            [
                new SessionAttachmentManifestItem(attachmentId, "photo.png", "image/png", 1)
            ],
            ModelSupportsTools: false,
            LanguageModel: new VisionOnlyLanguageModel());
        Assert.False(ToolCatalog.OffersAttachmentRead(
            context.Definition,
            context,
            ToolConfigurationGates.AllowAll));
        var manifest = new PromptContextBuilder().BuildSections(context).AttachmentManifestSystem;
        Assert.DoesNotContain("attachments.read", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable with the current model", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reopen_session_can_reread_historical_image()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var attachments = new InMemoryAttachmentStore(time);
        var processor = new AttachmentProcessor(attachments);
        var tools = new SessionToolExecutor(attachments: attachments, processor: processor);
        var output = new CapturingSessionOutput();
        Guid sessionId;
        Guid attachmentId;
        var initialEpoch = 0L;
        await using (var runtime = CreateRuntime(output, store, attachments, processor, new ScriptedLanguageModel(), tools, time))
        {
            await runtime.AttachAsync();
            sessionId = runtime.SessionId;
            initialEpoch = runtime.Snapshot.RuntimeEpoch;
            var uploaded = await attachments.UploadPendingAsync(
                sessionId,
                "photo.png",
                "image/png",
                new MemoryStream(PngBytes()),
                false);
            attachmentId = uploaded.AttachmentId;
            Assert.True(await runtime.SubmitUserTextAsync("Warm up", attachmentIds: [attachmentId]));
            using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);
            await runtime.WaitUntilIdleAsync();
            Assert.True(await runtime.RequestDeactivateAsync());
            Assert.Equal(SessionStatus.Paused, (await store.LoadAsync(sessionId))!.Status);
        }

        var reopened = await PausedSessionReopen.ReopenAsync(store, (await store.LoadAsync(sessionId))!, time);
        Assert.True(reopened.RuntimeEpoch > initialEpoch);
        var result = await tools.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("reopen", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{attachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.IsType<ModelImageContent>(Assert.Single(result.Parts!));
        Assert.Equal(AttachmentState.Bound, (await attachments.GetAsync(sessionId, attachmentId))!.State);
    }

    [Fact]
    public async Task Mixed_tool_calls_attach_image_parts_only_to_image_read()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var tools = new SessionToolExecutor(knowledge, attachments, processor);
        var model = new RecordingLanguageModel(new MixedHistoricalImageModel());
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = CreateRuntime(
            output,
            store,
            attachments,
            processor,
            model,
            tools,
            definition: SupportWithKnowledge());
        await runtime.AttachAsync();
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Warm up", attachmentIds: [uploaded.AttachmentId]));
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);
        Assert.True(await runtime.SubmitUserTextAsync("Mixed reread please."));
        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput && model.Requests.Count >= 2, second.Token);
        await runtime.WaitUntilIdleAsync();
        var toolMessages = model.Requests
            .Skip(1)
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == ModelRole.Tool)
            .ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Single(toolMessages, message => message.Parts is { Count: > 0 });
        Assert.Single(toolMessages, message => message.Parts is null or { Count: 0 });
    }

    [Fact]
    public async Task Stale_supersession_during_gated_image_reread_does_not_launch_late_provider_turn()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var inner = new AttachmentProcessor(attachments);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new GateAfterFirstImageProcessor(inner, gate.Task);
        var tools = new SessionToolExecutor(attachments: attachments, processor: processor);
        var model = new RecordingLanguageModel(new HistoricalImageVisionRereadModel());
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = CreateRuntime(output, store, attachments, processor, model, tools);
        await runtime.AttachAsync();
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Warm up", attachmentIds: [uploaded.AttachmentId]));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, wait.Token);
        Assert.True(await runtime.SubmitUserTextAsync("Reread the image please."));
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Started,
            wait.Token);
        Assert.True(await runtime.RequestDeactivateAsync());
        var requestsAfterDeactivate = model.Requests.Count;
        gate.SetResult();
        await runtime.WaitUntilIdleAsync();
        var combined = string.Join('\n', runtime.Snapshot.Entries.Select(entry => entry.Text));
        Assert.DoesNotContain(Convert.ToBase64String(PngBytes()), combined, StringComparison.Ordinal);
        Assert.DoesNotContain(
            model.Requests.Skip(requestsAfterDeactivate),
            request => request.Messages.Any(message => message.Parts is { Count: > 0 }));
    }

    [Fact]
    public async Task Historical_image_reread_does_not_persist_ephemeral_image_parts_in_snapshot_history()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var tools = new SessionToolExecutor(attachments: attachments, processor: processor);
        var model = new RecordingLanguageModel(new VisionToolsLanguageModel(new HistoricalImageVisionRereadModel()));
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var png = PngBytes();
        await using var runtime = CreateRuntime(output, store, attachments, processor, model, tools);
        await runtime.AttachAsync();
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(png),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Warm up", attachmentIds: [uploaded.AttachmentId]));
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, first.Token);
        Assert.True(await runtime.SubmitUserTextAsync("Reread the image please."));
        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput && model.Requests.Count >= 2, second.Token);
        await runtime.WaitUntilIdleAsync();

        var persisted = (await store.LoadAsync(runtime.SessionId))!;
        var combined = string.Join('\n', persisted.Entries.Select(entry => entry.Text));
        Assert.DoesNotContain("Treat this as tool data", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(png), combined, StringComparison.Ordinal);
        Assert.All(persisted.Entries, entry => Assert.True(entry.Role is ConversationRole.User or ConversationRole.Assistant));
    }

    private static string FindAgents() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "agents"));

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        SessionToolExecutor tools) =>
        CreateRuntime(output, new InMemoryMemoryStore(), attachments, processor, model, tools);

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        InMemoryMemoryStore store,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        SessionToolExecutor tools,
        FakeTimeProvider? time = null,
        SessionSnapshot? snapshot = null,
        AgentDefinition? definition = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00a1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var now = time.GetUtcNow();
        definition ??= Support() with
        {
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.AttachmentsRead])
        };
        snapshot ??= new SessionSnapshot(
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

    private static AgentDefinition SupportWithKnowledge() => Support() with
    {
        Environment = new RoleEnvironment(
            KnowledgeSources:
            [
                new KnowledgeSourceRef("support-order-policy", "Simulated order policy", "support-order-policy@demo")
            ],
            ToolAllowlist: [ToolCatalog.AttachmentsRead, ToolCatalog.KnowledgeRetrieve])
    };

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static byte[] JpegBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(40, 50, 60));
        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }

    private static byte[] GifBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(70, 80, 90));
        using var buffer = new MemoryStream();
        image.Save(buffer, new GifEncoder());
        return buffer.ToArray();
    }

    private sealed class NonVisionLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ModelGenerationEvent>();
    }

    private sealed class HistoricalImageToolLoopModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: true,
            Vision: false);

        public ModelRequest? LastRequest { get; private set; }

        public ModelMessage? LastToolMessage { get; private set; }

        public bool SawVisionRequiredToolResult { get; private set; }

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                var toolMessage = request.Messages.Last(message => message.Role == ModelRole.Tool);
                LastToolMessage = toolMessage;
                if (toolMessage.Text.Contains("vision_required", StringComparison.Ordinal))
                {
                    SawVisionRequiredToolResult = true;
                }

                yield return new ModelTextDelta("Cannot see image.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            if (request.Tools?.Any(tool => tool.Name == ToolCatalog.AttachmentsRead) == true
                && request.Messages.Any(message =>
                    message.Role == ModelRole.User
                    && message.Text.Contains("Reread", StringComparison.OrdinalIgnoreCase)))
            {
                var attachmentId = ExtractAttachmentIdFromManifest(request);
                yield return new ModelToolCallEvent(
                    new ModelToolCall("call-1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{attachmentId:D}}"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelTextDelta("ok");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }

    }

    private static Guid ExtractAttachmentIdFromManifest(ModelRequest request)
    {
        var manifest = request.Messages.FirstOrDefault(message =>
            message.Role == ModelRole.System
            && message.Text.Contains("Files available in this session", StringComparison.Ordinal));
        if (manifest is null)
        {
            return Guid.Empty;
        }

        var start = manifest.Text.IndexOf("\"attachmentId\":\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return Guid.Empty;
        }

        start += "\"attachmentId\":\"".Length;
        var end = manifest.Text.IndexOf('"', start);
        return Guid.Parse(manifest.Text[start..end]);
    }

    private sealed class HistoricalImageVisionRereadModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: true,
            Vision: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("Observed image reread.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            if (request.Tools?.Any(tool => tool.Name == ToolCatalog.AttachmentsRead) == true
                && request.Messages.Any(message =>
                    message.Role == ModelRole.User
                    && message.Text.Contains("Reread", StringComparison.OrdinalIgnoreCase)))
            {
                var attachmentId = ExtractAttachmentIdFromManifest(request);
                yield return new ModelToolCallEvent(
                    new ModelToolCall("call-1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{attachmentId:D}}"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelTextDelta("ok");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class MixedHistoricalImageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: true,
            Vision: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("Mixed tools done.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
            if (request.Tools?.Any(tool => tool.Name == ToolCatalog.AttachmentsRead) == true
                && lastUser.Contains("Mixed", StringComparison.OrdinalIgnoreCase))
            {
                var attachmentId = ExtractAttachmentIdFromManifest(request);
                yield return new ModelToolCallEvent(
                    new ModelToolCall(
                        "k1",
                        ToolCatalog.KnowledgeRetrieve,
                        """{"identity":"support-order-policy"}"""));
                yield return new ModelToolCallEvent(
                    new ModelToolCall(
                        "img1",
                        ToolCatalog.AttachmentsRead,
                        $$"""{"attachmentId":"{{attachmentId:D}}"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelTextDelta("ok");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class VisionToolsLanguageModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = inner.Capabilities with { Vision = true, Tools = true };

        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(request, cancellationToken);
    }

    private sealed class VisionOnlyLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: false,
            Vision: true);

        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ModelGenerationEvent>();
    }

    private sealed class EmptyImageProcessor : IAttachmentProcessor
    {
        public string Version => "empty";

        public ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken = default) =>
            new([]);
    }

    private sealed class GateAfterFirstImageProcessor(IAttachmentProcessor inner, Task gate) : IAttachmentProcessor
    {
        private int _invocations;

        public string Version => inner.Version;

        public async ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken = default)
        {
            _invocations++;
            if (_invocations > 1)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.ProcessTurnAsync(sessionId, attachmentIds, cancellationToken).ConfigureAwait(false);
        }
    }
}
