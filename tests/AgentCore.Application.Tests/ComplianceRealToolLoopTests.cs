using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class ComplianceRealToolLoopTests
{
    [Fact]
    public async Task Compliance_png_turn_executes_knowledge_tool_and_returns_cited_answer()
    {
        var handler = new ComplianceToolLoopHandler();
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
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var tools = new SessionToolExecutor(knowledge, attachments);
        var output = new CapturingSessionOutput();
        var definition = await LoadComplianceAsync();
        await using var runtime = CreateRuntime(output, attachments, processor, tools, model, definition);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "policy.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync(
            "Cite retention for this compliance case.",
            attachmentIds: [uploaded.AttachmentId]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, cts.Token);
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(2, handler.PostCount);
        Assert.Contains("\"name\":\"knowledge_retrieve\"", handler.FirstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"knowledge.retrieve\"", handler.FirstBody, StringComparison.Ordinal);
        Assert.Contains("image_url", handler.FirstBody, StringComparison.Ordinal);
        Assert.Contains("knowledge_retrieve", handler.SecondBody, StringComparison.Ordinal);
        Assert.Contains("compliance-retention@demo", handler.SecondBody, StringComparison.Ordinal);
        Assert.Contains("demonstration session", handler.SecondBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role\":\"tool\"", handler.SecondBody, StringComparison.Ordinal);

        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("demonstration session", assistant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compliance-retention@demo", assistant.Text, StringComparison.Ordinal);
    }

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static async Task<AgentDefinition> LoadComplianceAsync()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return await store.GetAsync("compliance") ?? throw new InvalidOperationException("compliance definition missing.");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        SessionToolExecutor tools,
        ILanguageModel model,
        AgentDefinition definition)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0012-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
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
            processor: processor,
            tools: tools);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed class ComplianceToolLoopHandler : HttpMessageHandler
    {
        public int PostCount { get; private set; }

        public string FirstBody { get; private set; } = "";

        public string SecondBody { get; private set; } = "";

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PostCount++;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (PostCount == 1)
            {
                FirstBody = LastBody;
                var body = Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"knowledge_retrieve\",\"arguments\":\"\"}}]}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"identity\\\":\\\"compliance-retention\\\"}\"}}]}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                    "data: [DONE]\n\n");
                return Sse(body);
            }

            SecondBody = LastBody;
            var answer = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Transcripts are kept for the current demonstration session. Cite compliance-retention@demo.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n");
            return Sse(answer);
        }

        private static HttpResponseMessage Sse(byte[] body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(body))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            };
    }
}
