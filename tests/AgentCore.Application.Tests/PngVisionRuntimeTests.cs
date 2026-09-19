using System.Net;
using System.Net.Http.Headers;
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
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class PngVisionRuntimeTests
{
    [Fact]
    public async Task Png_attachment_reaches_vision_enabled_model_request()
    {
        var handler = new CapturingOpenRouterHandler();
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/",
                DefaultModel = "deepseek/deepseek-v4.1-flash",
                ApiKey = "test-key",
                Vision = true,
                Tools = true
            },
            TimeProvider.System);
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments, processor, model);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Describe the image.", attachmentIds: [uploaded.AttachmentId]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await output.WaitForAsync(
            item => item.Payload is TextDeltaOutput or ErrorOutput or ResponseCompletedOutput,
            cts.Token);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, handler.PostCount);
        Assert.Contains("image_url", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", handler.LastBody, StringComparison.Ordinal);
    }

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0011-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
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

    private sealed class CapturingOpenRouterHandler : HttpMessageHandler
    {
        public int PostCount { get; private set; }

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PostCount++;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(body))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            };
        }
    }
}
