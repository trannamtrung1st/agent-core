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
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
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

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        SessionToolExecutor tools)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00a1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var store = new InMemoryMemoryStore();
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
                var attachmentId = ExtractAttachmentId(request);
                yield return new ModelToolCallEvent(
                    new ModelToolCall("call-1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{attachmentId:D}}"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelTextDelta("ok");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }

        private static Guid ExtractAttachmentId(ModelRequest request)
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
    }
}
