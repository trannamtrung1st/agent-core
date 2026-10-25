using AgentCore.Application.Context;
using SkiaSharp;
using System.Net;
using System.Net.Http.Headers;
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
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

// Owned success, source/processor refusal, pause/reopen, mixed results and stale-media
// cases live in TenantSessionAccessTests.AttachmentReads/AttachmentReadBoundaries/
// AttachmentRecall/HistoricalImageLifecycle. The offline wire mapping remains in
// OpenAICompatibleLanguageModelTests. Keep local capability admission and unowned refusal.
public sealed class HistoricalImageRereadTests
{
    [Fact]
    public async Task Unowned_cross_session_historical_image_read_is_forbidden()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor();
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
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
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
    public async Task Unowned_unknown_historical_image_read_is_forbidden()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var executor = new SessionToolExecutor();
        var result = await executor.ExecuteAsync(
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{Guid.NewGuid():D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Parts);
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
        var manifest = new PromptContextBuilder().BuildContext(context).Text(ContextSectionId.Attachments);
        Assert.DoesNotContain("attachments.read", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable with the current model", manifest, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] StructuredSse(string displayText)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["displayText"] = displayText,
            ["speech"] = new Dictionary<string, object?> { ["mode"] = "same", ["text"] = null },
            ["blocks"] = Array.Empty<object>()
        });
        var chunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["choices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?> { ["content"] = payload },
                    ["finish_reason"] = "stop"
                }
            }
        });
        return Encoding.UTF8.GetBytes("data: " + chunk + "\n\ndata: [DONE]\n\n");
    }

    private static byte[] ToolCallSse(Guid attachmentId)
    {
        var arguments = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["attachmentId"] = attachmentId.ToString("D")
        });
        var chunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["choices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["id"] = "call_img",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = "attachments_read",
                                    ["arguments"] = arguments
                                }
                            }
                        }
                    },
                    ["finish_reason"] = "tool_calls"
                }
            }
        });
        return Encoding.UTF8.GetBytes("data: " + chunk + "\n\ndata: [DONE]\n\n");
    }

    private sealed class QueuedChatHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _responses = new();

        public List<string> Bodies { get; } = [];

        public void Enqueue(byte[] body) => _responses.Enqueue(body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var bytes = _responses.Count == 0
                ? "data: [DONE]\n\n"u8.ToArray()
                : _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(bytes))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            };
        }
    }

    private static string FindAgents()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "agents"))) return Path.Combine(directory.FullName, "agents");
        throw new DirectoryNotFoundException("agents/");
    }

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
            now, AgentInstanceId: Guid.NewGuid());
        if (snapshot.Revision == 1 && snapshot.Entries.Count == 0)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return SessionRuntimeFixture.Create(
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
        2,
        "customer-support",
        1,
        new AgentIdentity("Sam", "Support", "Help.", "Warm"),
        ["Help"],
        "Help.",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 10000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences(null, null),
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
        using var image = new SKBitmap(2, 2);
        using var buffer = new MemoryStream();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        encoded.SaveTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] JpegBytes()
    {
        using var image = new SKBitmap(2, 2);
        using var buffer = new MemoryStream();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        encoded.SaveTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] GifBytes() => Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

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
        var manifest = request.Context!.Text(AgentCore.Application.Context.ContextSectionId.Attachments);
        var start = manifest.IndexOf("\"attachmentId\":\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return Guid.Empty;
        }

        start += "\"attachmentId\":\"".Length;
        var end = manifest.IndexOf('"', start);
        return Guid.Parse(manifest[start..end]);
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
