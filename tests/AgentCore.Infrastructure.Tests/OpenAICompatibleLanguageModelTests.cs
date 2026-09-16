using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenAICompatibleLanguageModelTests
{
    [Fact]
    public async Task Parses_chunks_split_across_reads_including_utf8()
    {
        var snowman = "☃"; // U+2603 e2 98 83
        var bytes = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"" + snowman + "\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n");
        var handler = new ScriptedHandler(Split(bytes, 3));
        var events = await CollectAsync(handler);
        Assert.Equal(["Hel", snowman], events.OfType<ModelTextDelta>().Select(delta => delta.Text).ToArray());
        Assert.IsType<ModelCompleted>(events[^1]);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Tolerates_crlf_comments_and_ignores_role_usage_and_reasoning()
    {
        var body = ": keep-alive\r\n" +
                   "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\",\"reasoning\":\"secret\"}}]}\r\n\r\n" +
                   "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}\r\n\r\n" +
                   "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\n" +
                   "data: [DONE]\r\n\r\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.Equal(["Hi"], events.OfType<ModelTextDelta>().Select(delta => delta.Text).ToArray());
        var completed = Assert.IsType<ModelCompleted>(events[^1]);
        Assert.Equal(3, completed.InputTokens);
        Assert.Equal(1, completed.OutputTokens);
        Assert.DoesNotContain(events, item => item is ModelTextDelta delta && delta.Text.Contains("secret"));
    }

    [Fact]
    public async Task Missing_finish_is_unavailable()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.Contains(events, item => item is ModelTextDelta);
        var failed = Assert.IsType<ModelFailed>(events[^1]);
        Assert.Equal(ProviderErrorCode.Unavailable, failed.Failure.Code);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Cancellation_before_completion_does_not_post_again()
    {
        var handler = new ScriptedHandler(
            [Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n")],
            holdOpen: true);
        using var cts = new CancellationTokenSource();
        var model = Create(handler);
        var listed = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(Request(), cts.Token))
        {
            listed.Add(item);
            if (item is ModelTextDelta)
            {
                await cts.CancelAsync();
            }
        }

        Assert.Equal(1, handler.PostCount);
        Assert.True(listed.OfType<ModelTextDelta>().Any() || listed.OfType<ModelFailed>().Any(failed => failed.Failure.Code == ProviderErrorCode.Cancelled));
    }

    [Fact]
    public async Task Rate_limit_maps_retry_after()
    {
        var handler = new StatusHandler(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7));
        var events = await CollectAsync(handler);
        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.RateLimited, failed.Failure.Code);
        Assert.Equal(TimeSpan.FromSeconds(7), failed.Failure.RetryAfter);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Midstream_error_does_not_replay_partial_or_repost()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" +
                   "data: {\"error\":{\"message\":\"boom\"}}\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.Equal("partial", Assert.IsType<ModelTextDelta>(events[0]).Text);
        Assert.IsType<ModelFailed>(events[^1]);
        Assert.Equal(1, handler.PostCount);
        await CollectAsync(handler);
        Assert.Equal(2, handler.PostCount);
    }

    [Fact]
    public async Task Duplicate_generate_is_a_new_post_not_a_retry_of_the_same_call()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"A\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = Create(handler);
        _ = await CollectAsync(model);
        _ = await CollectAsync(model);
        Assert.Equal(2, handler.PostCount);
    }

    [Fact]
    public async Task Maps_tool_call_fragments_when_tools_are_offered()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"knowledge_retrieve\",\"arguments\":\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"identity\\\":\\\"support-order-policy\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                Tools = true
            });
        var tools = new[]
        {
            new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Retrieve knowledge.", """{"type":"object"}""")
        };
        var events = await CollectAsync(model, new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")], Tools: tools));
        Assert.Contains("\"tools\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("knowledge_retrieve", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledge.retrieve", handler.LastBody, StringComparison.Ordinal);
        var call = Assert.IsType<ModelToolCallEvent>(events[0]).Call;
        Assert.Equal("call_1", call.Id);
        Assert.Equal(ToolCatalog.KnowledgeRetrieve, call.Name);
        Assert.Contains("support-order-policy", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(ModelStopReason.ToolCalls, Assert.IsType<ModelCompleted>(events[^1]).Reason);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public void ToolCatalog_wire_names_are_openai_compatible()
    {
        foreach (var name in ToolCatalog.AllKnownNames())
        {
            var wire = OpenAiCompatibleToolNames.ToWireName(name);
            Assert.True(OpenAiCompatibleToolNames.IsWireSafe(wire), wire);
            Assert.Equal(name, OpenAiCompatibleToolNames.ToCanonicalName(wire));
            Assert.DoesNotContain(".", wire, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unexpected_tool_calls_without_offered_tools_are_unsupported()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"process\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        var failed = Assert.IsType<ModelFailed>(events[0]);
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
    }

    [Fact]
    public void Factory_selects_scripted_or_openai_compatible_by_adapter()
    {
        var scripted = LanguageModelAdapterFactory.Create(new LanguageModelProviderOptions { Adapter = "Scripted" }, handler: null);
        Assert.IsType<Providers.Synthetic.ScriptedLanguageModel>(scripted);
        var compatible = LanguageModelAdapterFactory.Create(
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1:9/v1/",
                DefaultModel = "local-model"
            },
            new ScriptedHandler(
            [
                Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"X\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")
            ]));
        Assert.IsType<OpenAICompatibleLanguageModel>(compatible);
    }

    [Fact]
    public void OpenRouter_path_preserves_api_prefix()
    {
        var uri = OpenAICompatibleLanguageModel.JoinCompletions("https://openrouter.ai/api/v1/");
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", uri.AbsoluteUri);
    }

    [Fact]
    public async Task Malformed_payload_is_unavailable_without_a_second_post()
    {
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes("data: {not-json\n\n")]);
        var events = await CollectAsync(handler);
        Assert.Equal(ProviderErrorCode.Unavailable, Assert.IsType<ModelFailed>(Assert.Single(events)).Failure.Code);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Oversized_sse_event_is_unavailable()
    {
        var payload = "data: " + new string('x', (1024 * 1024) + 8) + "\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(payload)]);
        var events = await CollectAsync(handler);
        Assert.Equal(ProviderErrorCode.Unavailable, Assert.IsType<ModelFailed>(Assert.Single(events)).Failure.Code);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task Unauthorized_maps_authentication()
    {
        var handler = new StatusHandler(HttpStatusCode.Unauthorized, TimeSpan.FromSeconds(1));
        var failed = Assert.IsType<ModelFailed>(Assert.Single(await CollectAsync(handler)));
        Assert.Equal(ProviderErrorCode.Authentication, failed.Failure.Code);
    }

    [Fact]
    public async Task Service_unavailable_maps_unavailable()
    {
        var handler = new StatusHandler(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(1));
        var failed = Assert.IsType<ModelFailed>(Assert.Single(await CollectAsync(handler)));
        Assert.Equal(ProviderErrorCode.Unavailable, failed.Failure.Code);
    }

    [Fact]
    public void Synthetic_profile_keeps_scripted_when_compatible_options_are_present()
    {
        using var provider = BuildInfrastructure(
            "Synthetic",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model"
            });
        Assert.IsType<Providers.Synthetic.ScriptedLanguageModel>(provider.GetRequiredService<ILanguageModel>());
    }

    [Fact]
    public void Real_profile_resolves_compatible_adapter_from_http_client_factory()
    {
        using var provider = BuildInfrastructure(
            "Real",
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model"
            });
        Assert.IsType<OpenAICompatibleLanguageModel>(provider.GetRequiredService<ILanguageModel>());
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAICompatibleLanguageModel.HttpClientName));
    }

    [Fact]
    public void Default_model_in_committed_settings_is_not_openrouter_free()
    {
        var settings = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/AgentCore.Api/appsettings.json"));
        Assert.DoesNotContain("openrouter/free", settings, StringComparison.Ordinal);
        Assert.Contains("\"Adapter\": \"Scripted\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_smoke_skips_unless_opt_in_and_key_are_both_present()
    {
        var attr = new LiveProviderFactAttribute();
        var optedIn = string.Equals(
            Environment.GetEnvironmentVariable("AGENTCORE_LIVE_PROVIDER_TESTS"),
            "1",
            StringComparison.Ordinal);
        var hasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
        if (!(optedIn && hasKey))
        {
            Assert.False(string.IsNullOrEmpty(attr.Skip));
        }
    }

    [Fact]
    public void Application_and_contracts_do_not_contain_provider_dtos_or_keys()
    {
        var root = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src/AgentCore.Application"), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "src/AgentCore.Domain"), "*.cs", SearchOption.AllDirectories))
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "src/AgentCore.Contracts"), "*.cs", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("chat/completions", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OPENROUTER_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("openrouter/free", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Session_runtime_accepts_compatible_model_without_application_changes()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = Create(handler);
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var definition = new AgentDefinition(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "Speaking examiner", "desc", "tone"),
            ["goal"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1, ids.NewSessionId(), 1, definition, SessionMode.Text, null,
            SessionStatus.Created, [], string.Empty, 0, null, null, now, now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.TextDeltas, delta => delta.Text == "Hello");
        Assert.Equal(1, handler.PostCount);
    }

    [LiveProviderFact]
    public async Task OpenRouter_free_smoke_is_opt_in_only()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        using var http = new HttpClient();
        var model = new OpenAICompatibleLanguageModel(
            http,
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "https://openrouter.ai/api/v1/",
                ApiKey = key,
                DefaultModel = "openrouter/free",
                Timeouts = new ProviderTimeoutOptions { SetupSeconds = 10, StreamIdleSeconds = 20, TotalSeconds = 45 }
            });
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(
                           new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Reply with the word ok.")])))
        {
            events.Add(item);
        }

        Assert.DoesNotContain(events, item => item is ModelFailed failed && failed.Failure.Code == ProviderErrorCode.Authentication);
        Assert.Contains(events, item => item is ModelTextDelta or ModelCompleted or ModelFailed);
    }

    [Fact]
    public async Task Vision_false_returns_typed_unsupported_without_http()
    {
        var handler = new ScriptedHandler(
            [Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"no\"}}]}\n\n")]);
        var model = Create(handler);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(
                    ModelRole.User,
                    "see",
                    [new ModelImageContent("image/png", [1, 2, 3], "x.png")])
            ]);
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request))
        {
            events.Add(item);
        }

        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public async Task Tools_false_returns_typed_unsupported_without_http()
    {
        var handler = new ScriptedHandler(
            [Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"no\"}}]}\n\n")]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                Tools = false
            });
        var tools = new[] { new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Retrieve knowledge.", """{"type":"object"}""") };
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")], Tools: tools)))
        {
            events.Add(item);
        }

        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public async Task Vision_true_maps_normalized_image_parts()
    {
        var handler = new ScriptedHandler(
            [Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n")]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                Vision = true
            });
        var png = Convert.ToBase64String("img"u8.ToArray());
        await foreach (var _ in model.GenerateAsync(
                           new ModelRequest(
                               Guid.NewGuid(),
                               [
                                   new ModelMessage(
                                       ModelRole.User,
                                       "see",
                                       [
                                           new ModelTextContent("see"),
                                           new ModelImageContent("image/png", "img"u8.ToArray(), "x.png")
                                       ])
                               ])))
        {
        }

        Assert.Equal(1, handler.PostCount);
        Assert.Contains("image_url", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains($"data:image/png;base64,{png}", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI.Chat", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multipart_user_message_preserves_instruction_and_attachment_on_wire()
    {
        var handler = new ScriptedHandler(
            [Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n")]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                Vision = true,
                Tools = true
            });
        var message = PromptContextBuilder.BuildCurrentUserMessage(
            "Summarize the attachment",
            [
                new AttachmentProcessResult(
                    Guid.NewGuid(),
                    AttachmentLimits.ProcessorVersion,
                    AttachmentProcessKind.ExtractedText,
                    "policy.md",
                    "text/markdown",
                    "Retention policy details for the current session.",
                    null,
                    null,
                    null)
            ],
            attachmentsReadAvailable: false);
        await foreach (var _ in model.GenerateAsync(new ModelRequest(Guid.NewGuid(), [message])))
        {
        }

        Assert.Equal(1, handler.PostCount);
        Assert.Contains("Summarize the attachment", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("Retention policy details", handler.LastBody, StringComparison.Ordinal);
    }

    private static async Task<List<ModelGenerationEvent>> CollectAsync(HttpMessageHandler handler)
        => await CollectAsync(Create(handler));

    private static async Task<List<ModelGenerationEvent>> CollectAsync(ILanguageModel model, ModelRequest? request = null)
    {
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request ?? Request()))
        {
            events.Add(item);
        }

        return events;
    }

    private static OpenAICompatibleLanguageModel Create(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key"
            });

    private static ModelRequest Request() =>
        new(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")]);

    private static ServiceProvider BuildInfrastructure(string profile, LanguageModelProviderOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAgentCoreInfrastructure(FindAgents(), profile, options);
        return services.BuildServiceProvider();
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(FindRepoRoot());
        return Path.Combine(dir.FullName, "agents");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private static IReadOnlyList<byte[]> Split(byte[] bytes, int size) =>
        Enumerable.Range(0, (bytes.Length + size - 1) / size)
            .Select(index => bytes.Skip(index * size).Take(size).ToArray())
            .ToArray();
}

public sealed class LiveProviderFactAttribute : FactAttribute
{
    public LiveProviderFactAttribute()
    {
        var optIn = string.Equals(Environment.GetEnvironmentVariable("AGENTCORE_LIVE_PROVIDER_TESTS"), "1", StringComparison.Ordinal);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!optIn)
        {
            Skip = "Opt-in AGENTCORE_LIVE_PROVIDER_TESTS=1 is required; default suites must not call OpenRouter.";
        }
        else if (string.IsNullOrWhiteSpace(key))
        {
            Skip = "OPENROUTER_API_KEY is missing.";
        }
    }
}

internal class ScriptedHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<byte[]> _chunks;
    private readonly bool _holdOpen;

    public ScriptedHandler(IReadOnlyList<byte[]> chunks, bool holdOpen = false)
    {
        _chunks = chunks;
        _holdOpen = holdOpen;
    }

    public int PostCount { get; private set; }

    public string LastBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        PostCount++;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
        Assert.Contains("/chat/completions", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        LastBody = body;
        Assert.Contains("\"stream\":true", body.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", body, StringComparison.Ordinal);
        var stream = new ChunkedStream(_chunks, _holdOpen, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
            }
        };
    }
}

internal sealed class StatusHandler(HttpStatusCode status, TimeSpan retryAfter) : HttpMessageHandler
{
    public int PostCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        PostCount++;
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return Task.FromResult(response);
    }
}

internal sealed class ChunkedStream : Stream
{
    private readonly IReadOnlyList<byte[]> _chunks;
    private readonly bool _holdOpen;
    private readonly CancellationToken _cancellation;
    private int _index;

    public ChunkedStream(IReadOnlyList<byte[]> chunks, bool holdOpen, CancellationToken cancellation)
    {
        _chunks = chunks;
        _holdOpen = holdOpen;
        _cancellation = cancellation;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation);
        if (_index >= _chunks.Count)
        {
            if (_holdOpen)
            {
                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            }

            return 0;
        }

        var chunk = _chunks[_index++];
        var copied = Math.Min(buffer.Length, chunk.Length);
        chunk.AsSpan(0, copied).CopyTo(buffer.Span);
        return copied;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
