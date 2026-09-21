using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.Contains(events, item => item is ModelReasoningDelta delta && delta.Text == "secret");
        var completed = Assert.IsType<ModelCompleted>(events[^1]);
        Assert.Equal(3, completed.InputTokens);
        Assert.Equal(1, completed.OutputTokens);
        Assert.DoesNotContain(events, item => item is ModelTextDelta delta && delta.Text.Contains("secret"));
    }

    [Fact]
    public async Task Reasoning_only_delta_emits_reasoning_event_not_text()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"reasoning\":\"private planning\"}}]}\n\n" +
                   "data: {\"choices\":[{\"delta\":{\"content\":\"Final answer.\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.DoesNotContain(events, item => item is ModelTextDelta delta && delta.Text.Contains("private", StringComparison.Ordinal));
        Assert.Contains(events, item => item is ModelReasoningDelta delta && delta.Text == "private planning");
        Assert.Equal("Final answer.", Assert.Single(events.OfType<ModelTextDelta>()).Text);
    }

    [Fact]
    public async Task Reasoning_details_only_delta_emits_reasoning_not_text()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"hidden step\"}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Visible.\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.Contains(events, item => item is ModelReasoningDelta delta && delta.Text == "hidden step");
        Assert.Equal("Visible.", Assert.Single(events.OfType<ModelTextDelta>()).Text);
    }

    [Fact]
    public async Task Mixed_reasoning_and_content_delta_splits_channels()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"plan\",\"content\":\"Could you tell me about your hometown?\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var events = await CollectAsync(handler);
        Assert.Equal("Could you tell me about your hometown?", Assert.Single(events.OfType<ModelTextDelta>()).Text);
        Assert.Equal("plan", Assert.Single(events.OfType<ModelReasoningDelta>()).Text);
    }

    [Fact]
    public async Task Tool_call_with_reasoning_keeps_tool_semantics_and_isolates_reasoning()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"pick tool\",\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"knowledge_retrieve\",\"arguments\":\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"identity\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
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
        Assert.Contains(events, item => item is ModelReasoningDelta);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
        Assert.NotNull(events.OfType<ModelToolCallEvent>().SingleOrDefault());
        Assert.Equal(ModelStopReason.ToolCalls, Assert.IsType<ModelCompleted>(events[^1]).Reason);
    }

    [Fact]
    public async Task OpenRouter_reasoning_object_wire_sends_effort_and_exclude()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "deepseek/deepseek-v4.1-flash",
                ReasoningObjectWire = true,
                ExcludeVisibleReasoning = true,
                ApiKey = "test-key"
            });
        _ = await CollectAsync(model, new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")], ReasoningEffort: "medium"));
        Assert.Contains("\"reasoning\":", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"effort\":\"medium\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"exclude\":true", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_choice_diagnostic_records_field_presence_without_content()
    {
        var diagnostics = new List<StreamChoiceDiagnostic>();
        var body =
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"secret\",\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
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
                StreamChoiceDiagnostic = diagnostics.Add
            });
        _ = await CollectAsync(model);
        Assert.Equal(2, diagnostics.Count);
        Assert.True(diagnostics[0].Reasoning);
        Assert.True(diagnostics[0].Content);
        Assert.False(diagnostics[0].ToolCalls);
        Assert.Equal("stop", diagnostics[1].FinishReason);
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
    public async Task Sends_configured_reasoning_effort_on_chat_completions()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ReasoningEffort = "medium",
                ApiKey = "test-key"
            });
        _ = await CollectAsync(model);
        Assert.Contains("\"reasoning_effort\":\"medium\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_reasoning_effort_overrides_configured_fallback()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
                   "data: [DONE]\n\n";
        var handler = new ScriptedHandler([Encoding.UTF8.GetBytes(body)]);
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ReasoningEffort = "medium",
                ApiKey = "test-key"
            });
        _ = await CollectAsync(model, new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")], ReasoningEffort: "high"));
        Assert.Contains("\"reasoning_effort\":\"high\"", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning_effort\":\"medium\"", handler.LastBody, StringComparison.Ordinal);
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
            Create(handler),
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
    public async Task Gpt4oMini_historical_image_follow_up_is_opt_in_only()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var logger = new ListLogger();
        using var http = new HttpClient();
        var model = new OpenAICompatibleLanguageModel(
            http,
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "https://openrouter.ai/api/v1/",
                ApiKey = key,
                DefaultModel = "openai/gpt-4o-mini-2024-07-18",
                Vision = true,
                Tools = true,
                StructuredOutput = true,
                Timeouts = new ProviderTimeoutOptions { SetupSeconds = 20, StreamIdleSeconds = 30, TotalSeconds = 60 }
            },
            logger: logger);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(
                           new ModelRequest(
                               Guid.NewGuid(),
                               [
                                   new ModelMessage(ModelRole.User, "Review the image again."),
                                   new ModelMessage(
                                       ModelRole.Assistant,
                                       string.Empty,
                                       ToolCalls: [new ModelToolCall("call_img", ToolCatalog.AttachmentsRead, """{"attachmentId":"019944af-0001-7000-8000-000000000001"}""")]),
                                   new ModelMessage(
                                       ModelRole.Tool,
                                       """{"kind":"image","contentProvided":true}""",
                                       [new ModelImageContent("image/png", png, "pixel.png")],
                                       ToolCallId: "call_img",
                                       Name: ToolCatalog.AttachmentsRead)
                               ],
                               MaxOutputTokens: 128,
                               Tools: [new ModelToolDefinition(ToolCatalog.AttachmentsRead, "Read one session attachment.", """{"type":"object"}""")],
                               ResponseContract: new ModelResponseContract(false))))
        {
            events.Add(item);
        }

        var rejected = events.OfType<ModelFailed>()
            .FirstOrDefault(item => item.Failure.Code == ProviderErrorCode.InvalidRequest);
        Assert.True(
            rejected is null,
            (rejected?.Failure.SafeMessage ?? "rejected") + " " + string.Join(" | ", logger.Messages));
        Assert.Contains(events, item => item is ModelTextDelta or ModelCompleted);
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

    [LiveProviderFact]
    public async Task DeepSeek_v41_reasoning_probe_records_wire_field_presence()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var diagnostics = new List<StreamChoiceDiagnostic>();
        using var http = new HttpClient();
        var model = new OpenAICompatibleLanguageModel(
            http,
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "https://openrouter.ai/api/v1/",
                ApiKey = key,
                DefaultModel = "deepseek/deepseek-v4.1-flash",
                ReasoningObjectWire = true,
                ExcludeVisibleReasoning = true,
                StreamChoiceDiagnostic = diagnostics.Add,
                Timeouts = new ProviderTimeoutOptions { SetupSeconds = 15, StreamIdleSeconds = 30, TotalSeconds = 60 }
            });
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(
                           new ModelRequest(
                               Guid.NewGuid(),
                               [new ModelMessage(ModelRole.User, "Reply with one short sentence asking what city I grew up in.")],
                               MaxOutputTokens: 256,
                               ReasoningEffort: "medium")))
        {
            events.Add(item);
        }

        Assert.DoesNotContain(events, item => item is ModelFailed failed && failed.Failure.Code == ProviderErrorCode.Authentication);
        Assert.Contains(events, item => item is ModelCompleted);
        var reasoningDeltas = events.OfType<ModelReasoningDelta>().Count();
        var textDeltas = events.OfType<ModelTextDelta>().Select(delta => delta.Text).ToArray();
        var sawReasoningField = diagnostics.Any(item => item.Reasoning || item.ReasoningDetails);
        var sawContentField = diagnostics.Any(item => item.Content);
        Assert.True(
            sawReasoningField || reasoningDeltas > 0 || sawContentField,
            $"Probe saw no SSE choice fields. diagnostics={diagnostics.Count} reasoningDeltas={reasoningDeltas} textDeltas={textDeltas.Length}");
        if (sawReasoningField || reasoningDeltas > 0)
        {
            Assert.True(
                reasoningDeltas > 0,
                "Provider sent separate reasoning fields on the wire; adapter must emit ModelReasoningDelta.");
            Assert.DoesNotContain(textDeltas, text => text.Contains("reasoning_details", StringComparison.Ordinal));
        }
    }

    [LiveProviderFact]
    public async Task DeepSeek_v41_conversational_probe_keeps_reasoning_out_of_display_and_history()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var general = (await store.GetAsync("general-assistant"))!;
        var examiner = (await store.GetAsync("examiner"))!;

        await ProbeAgent(general, "Hello");
        await ProbeAgent(examiner, "Hi");

        async Task ProbeAgent(AgentDefinition definition, string userText)
        {
            var diagnostics = new List<StreamChoiceDiagnostic>();
            using var http = new HttpClient();
            var model = new OpenAICompatibleLanguageModel(
                http,
                new LanguageModelProviderOptions
                {
                    Adapter = "OpenAICompatible",
                    BaseUrl = "https://openrouter.ai/api/v1/",
                    ApiKey = key,
                    DefaultModel = "deepseek/deepseek-v4.1-flash",
                    ReasoningEffort = "medium",
                    ReasoningObjectWire = true,
                    ExcludeVisibleReasoning = true,
                    StreamChoiceDiagnostic = diagnostics.Add,
                    Timeouts = new ProviderTimeoutOptions { SetupSeconds = 15, StreamIdleSeconds = 30, TotalSeconds = 90 }
                });
            var recording = new RecordingLanguageModel(model);
            var output = new CapturingSessionOutput();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));
            var ids = new DeterministicIdGenerator(
                Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
                [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
            var memory = new InMemoryMemoryStore();
            var now = time.GetUtcNow();
            var snapshot = new SessionSnapshot(
                1, ids.NewSessionId(), 1, definition, SessionMode.Text, null,
                SessionStatus.Created, [], string.Empty, 0, null, null, now, now,
                ModelSelection: new SessionModelSelection(
                    "deepseek-v41-flash",
                    "primary-llm",
                    "deepseek/deepseek-v4.1-flash",
                    ModelSelectionSource.SystemDefault,
                    "medium"));
            await memory.SaveAsync(snapshot, 0);
            await using var runtime = new SessionRuntime(
                snapshot,
                recording,
                new DefaultAgentBrain(new PromptContextBuilder()),
                memory,
                output,
                ids,
                time,
                NullLogger<SessionRuntime>.Instance);

            await runtime.SubmitUserTextAsync(userText);
            await runtime.WaitUntilIdleAsync();

            var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            var displayText = string.Concat(output.TextDeltas.Select(delta => delta.Text));
            var completed = output.Terminals.LastOrDefault();

            Assert.NotNull(completed);
            Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
            Assert.Equal(assistant.Text, displayText);
            Assert.Null(assistant.Envelope?.SpeechText);

            var sawReasoningField = diagnostics.Any(d => d.Reasoning || d.ReasoningDetails);
            var sawContent = diagnostics.Any(d => d.Content);
            Assert.True(sawReasoningField || sawContent, $"Probe for '{definition.Id}' saw no SSE choice fields.");
            Assert.Contains(recording.Events, item => item is ModelCompleted);

            // Reasoning channel text must never appear in public text, history, or speech projection.
            foreach (var reasoning in recording.Events.OfType<ModelReasoningDelta>().Select(delta => delta.Text))
            {
                Assert.DoesNotContain(reasoning, assistant.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(reasoning, displayText, StringComparison.Ordinal);
            }

            // If the provider emitted planning inside delta.content, that is provider/model behavior;
            // we do not classify it with heuristics. The wire diagnostic still records field presence.
        }
    }

    private sealed class RecordingLanguageModel(ILanguageModel inner) : ILanguageModel
    {
        public List<ModelGenerationEvent> Events { get; } = [];

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in inner.GenerateAsync(request, cancellationToken))
            {
                Events.Add(item);
                yield return item;
            }
        }
    }

    [LiveProviderFact]
    public async Task OpenRouter_deepseek_v41_default_accepts_tools_request()
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
                DefaultModel = "deepseek/deepseek-v4.1-flash",
                ReasoningEffort = "medium",
                Tools = true,
                Vision = false,
                Timeouts = new ProviderTimeoutOptions { SetupSeconds = 15, StreamIdleSeconds = 30, TotalSeconds = 60 }
            });
        var tools = new[]
        {
            new ModelToolDefinition(
                ToolCatalog.KnowledgeRetrieve,
                "Retrieve approved knowledge by identity.",
                """{"type":"object","properties":{"identity":{"type":"string"}},"required":["identity"]}""")
        };
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(
                           new ModelRequest(
                               Guid.NewGuid(),
                               [new ModelMessage(ModelRole.User, "Call knowledge_retrieve with identity support-order-policy only.")],
                               Tools: tools,
                               ReasoningEffort: "medium")))
        {
            events.Add(item);
        }

        Assert.DoesNotContain(events, item => item is ModelFailed failed && failed.Failure.Code == ProviderErrorCode.Authentication);
        Assert.True(
            events.Any(item => item is ModelToolCallEvent)
            || events.Any(item => item is ModelCompleted),
            "Expected the pinned DeepSeek V4.1 model to accept the tools request and complete or tool-call.");
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
        Assert.DoesNotContain("response_format", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_contract_sends_strict_json_schema_response_format()
    {
        var handler = StopStream();
        var model = Create(handler, structuredOutput: true, tools: true, reasoningObject: true);
        var tools = new[]
        {
            new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Retrieve knowledge.", """{"type":"object"}""")
        };
        var request = new ModelRequest(
            Guid.NewGuid(),
            [new ModelMessage(ModelRole.User, "Hi")],
            Tools: tools,
            ReasoningEffort: "medium",
            ResponseContract: new ModelResponseContract(SpeechWillBeUsed: false));
        var events = await CollectAsync(model, request);
        Assert.Contains(events, item => item is ModelTextDelta or ModelCompleted);
        Assert.Contains("\"response_format\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"json_schema\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("agent_core_assistant_response", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", handler.LastBody.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("displayText", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("attachmentReference", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("blockId", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayDelivered", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("You are", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("examiner", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"tools\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"effort\":\"medium\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fallback_model_or_missing_contract_omits_response_format()
    {
        var fallback = StopStream();
        await CollectAsync(
            Create(fallback, structuredOutput: false),
            new ModelRequest(
                Guid.NewGuid(),
                [new ModelMessage(ModelRole.User, "Hi")],
                ResponseContract: new ModelResponseContract(SpeechWillBeUsed: true)));
        Assert.DoesNotContain("response_format", fallback.LastBody, StringComparison.Ordinal);

        var unstructuredRequest = StopStream();
        await CollectAsync(Create(unstructuredRequest, structuredOutput: true), Request());
        Assert.DoesNotContain("response_format", unstructuredRequest.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("json_schema", unstructuredRequest.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_contract_preserves_length_and_content_filter_finish_reasons()
    {
        var length = new ScriptedHandler(
        [
            Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\n" +
                "data: [DONE]\n\n")
        ]);
        var lengthEvents = await CollectAsync(
            Create(length, structuredOutput: true),
            new ModelRequest(
                Guid.NewGuid(),
                [new ModelMessage(ModelRole.User, "Hi")],
                ResponseContract: new ModelResponseContract(false)));
        Assert.Equal(ModelStopReason.LengthLimit, Assert.IsType<ModelCompleted>(lengthEvents[^1]).Reason);
        Assert.Contains("response_format", length.LastBody, StringComparison.Ordinal);

        var filtered = new ScriptedHandler(
        [
            Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\n" +
                "data: [DONE]\n\n")
        ]);
        var filterEvents = await CollectAsync(
            Create(filtered, structuredOutput: true),
            new ModelRequest(
                Guid.NewGuid(),
                [new ModelMessage(ModelRole.User, "Hi")],
                ResponseContract: new ModelResponseContract(false)));
        Assert.Equal(ModelStopReason.ContentFiltered, Assert.IsType<ModelCompleted>(filterEvents[^1]).Reason);
    }

    [Fact]
    public async Task Image_bearing_tool_result_maps_tool_text_then_wire_only_user_multipart()
    {
        var handler = StopStream();
        var model = Create(handler, tools: true, vision: true);
        var png = "img"u8.ToArray();
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "continue"),
                new ModelMessage(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls: [new ModelToolCall("call_1", ToolCatalog.AttachmentsRead, """{"attachmentId":"019944af-0001-7000-8000-000000000001"}""")]),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"kind":"image","contentProvided":true}""",
                    [new ModelImageContent("image/png", png, "x.png")],
                    ToolCallId: "call_1",
                    Name: ToolCatalog.AttachmentsRead)
            ],
            Tools: [new ModelToolDefinition(ToolCatalog.AttachmentsRead, "Read attachment.", """{"type":"object"}""")]);
        await CollectAsync(model, request);

        using var document = JsonDocument.Parse(handler.LastBody);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.DoesNotContain(Convert.ToBase64String(png), messages[2].GetRawText(), StringComparison.Ordinal);
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        var continuation = messages[3].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, continuation.ValueKind);
        Assert.Contains(
            "tool data",
            continuation[0].GetProperty("text").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("image_url", continuation[1].GetProperty("type").GetString());
        Assert.Contains($"data:image/png;base64,{Convert.ToBase64String(png)}", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_tool_results_keep_tool_messages_together_before_image_continuations()
    {
        var handler = StopStream();
        var model = Create(handler, tools: true, vision: true);
        var png = "img"u8.ToArray();
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "continue"),
                new ModelMessage(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ModelToolCall("call_1", ToolCatalog.AttachmentsRead, """{"attachmentId":"019944af-0001-7000-8000-000000000001"}"""),
                        new ModelToolCall("call_2", ToolCatalog.KnowledgeRetrieve, """{"identity":"x"}""")
                    ]),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"kind":"image","contentProvided":true}""",
                    [new ModelImageContent("image/png", png, "x.png")],
                    ToolCallId: "call_1",
                    Name: ToolCatalog.AttachmentsRead),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"identity":"x","content":"ok"}""",
                    ToolCallId: "call_2",
                    Name: ToolCatalog.KnowledgeRetrieve)
            ],
            Tools:
            [
                new ModelToolDefinition(ToolCatalog.AttachmentsRead, "Read attachment.", """{"type":"object"}"""),
                new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Knowledge.", """{"type":"object"}""")
            ]);
        await CollectAsync(model, request);

        using var document = JsonDocument.Parse(handler.LastBody);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_2", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("user", messages[4].GetProperty("role").GetString());
        Assert.Contains(
            "call_1",
            messages[4].GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("image_url", messages[4].GetProperty("content")[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Text_only_tool_result_keeps_single_tool_wire_message()
    {
        var handler = StopStream();
        var model = Create(handler, tools: true, vision: true);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "continue"),
                new ModelMessage(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls: [new ModelToolCall("call_1", ToolCatalog.KnowledgeRetrieve, """{"identity":"x"}""")]),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"identity":"x","content":"ok"}""",
                    ToolCallId: "call_1",
                    Name: ToolCatalog.KnowledgeRetrieve)
            ],
            Tools: [new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Knowledge.", """{"type":"object"}""")]);
        await CollectAsync(model, request);

        using var document = JsonDocument.Parse(handler.LastBody);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Rejected_follow_up_logs_sanitized_provider_reason_and_request_telemetry()
    {
        var png = "img"u8.ToArray();
        var handler = new ErrorBodyHandler(
            HttpStatusCode.BadRequest,
            """
            {"error":{"message":"invalid message sequence data:image/png;base64,QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo= bearer sk-live-secret user@example.com","code":"invalid_request_error","type":"invalid_request_error"}}
            """);
        var logger = new ListLogger();
        var model = new OpenAICompatibleLanguageModel(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "openai/gpt-4o-mini-2024-07-18",
                ApiKey = "test-key",
                Tools = true,
                Vision = true,
                StructuredOutput = true
            },
            logger: logger);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "review the image again"),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"kind":"image","contentProvided":true}""",
                    [new ModelImageContent("image/png", png, "x.png")],
                    ToolCallId: "call_1",
                    Name: ToolCatalog.AttachmentsRead)
            ],
            Tools: [new ModelToolDefinition(ToolCatalog.AttachmentsRead, "Read attachment.", """{"type":"object"}""")],
            ResponseContract: new ModelResponseContract(false));
        var events = await CollectAsync(model, request);
        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.InvalidRequest, failed.Failure.Code);
        Assert.Equal("Provider rejected follow-up request (400).", failed.Failure.SafeMessage);
        Assert.DoesNotContain("QUJD", failed.Failure.SafeMessage, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Messages);
        Assert.Contains("invalid message sequence", logged, StringComparison.Ordinal);
        Assert.Contains("openai/gpt-4o-mini-2024-07-18", logged, StringComparison.Ordinal);
        Assert.Contains("follow-up", logged, StringComparison.Ordinal);
        Assert.Contains("[image]", logged, StringComparison.Ordinal);
        Assert.Contains("[secret]", logged, StringComparison.Ordinal);
        Assert.Contains("[email]", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("QUJD", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-secret", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", logged, StringComparison.Ordinal);
        var telemetry = RuntimeTelemetry.SnapshotTimeline()
            .Last(item => item.Stage == OpenAICompatibleLanguageModel.RequestTelemetryStage
                && item.Detail?.Contains(request.ResponseId.ToString("D"), StringComparison.Ordinal) == true);
        Assert.Contains("httpStatus=400", telemetry.Detail, StringComparison.Ordinal);
        Assert.Contains("hasImage=True", telemetry.Detail, StringComparison.Ordinal);
        Assert.Contains($"imageBytes={png.Length}", telemetry.Detail, StringComparison.Ordinal);
        Assert.Contains("structuredOutput=True", telemetry.Detail, StringComparison.Ordinal);
        Assert.Contains("phase=follow-up", telemetry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("review the image again", telemetry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(png), telemetry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_vision_model_rejects_tool_image_parts_before_http()
    {
        var handler = StopStream();
        var model = Create(handler, tools: true, vision: false);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "continue"),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"kind":"image"}""",
                    [new ModelImageContent("image/png", "img"u8.ToArray(), "x.png")],
                    ToolCallId: "call_1",
                    Name: ToolCatalog.AttachmentsRead)
            ],
            Tools: [new ModelToolDefinition(ToolCatalog.AttachmentsRead, "Read attachment.", """{"type":"object"}""")]);
        var events = await CollectAsync(model, request);
        Assert.Equal(0, handler.PostCount);
        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
    }

    [Fact]
    public async Task Structured_contract_preserves_tool_call_and_image_mapping()
    {
        var toolsBody =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"knowledge_retrieve\",\"arguments\":\"{\\\"identity\\\":\\\"x\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var toolHandler = new ScriptedHandler([Encoding.UTF8.GetBytes(toolsBody)]);
        var tools = new[]
        {
            new ModelToolDefinition(ToolCatalog.KnowledgeRetrieve, "Retrieve knowledge.", """{"type":"object"}""")
        };
        var toolEvents = await CollectAsync(
            Create(toolHandler, structuredOutput: true, tools: true),
            new ModelRequest(
                Guid.NewGuid(),
                [new ModelMessage(ModelRole.User, "Hi")],
                Tools: tools,
                ResponseContract: new ModelResponseContract(false)));
        Assert.NotNull(toolEvents.OfType<ModelToolCallEvent>().SingleOrDefault());
        Assert.Equal(ModelStopReason.ToolCalls, Assert.IsType<ModelCompleted>(toolEvents[^1]).Reason);
        Assert.Contains("response_format", toolHandler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", toolHandler.LastBody, StringComparison.Ordinal);

        var png = Convert.ToBase64String("img"u8.ToArray());
        var vision = new ScriptedHandler(
        [
            Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n")
        ]);
        await CollectAsync(
            Create(vision, structuredOutput: true, vision: true),
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
                ],
                ResponseContract: new ModelResponseContract(false)));
        Assert.Contains("image_url", vision.LastBody, StringComparison.Ordinal);
        Assert.Contains($"data:image/png;base64,{png}", vision.LastBody, StringComparison.Ordinal);
        Assert.Contains("response_format", vision.LastBody, StringComparison.Ordinal);
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

    private static ScriptedHandler StopStream() =>
        new(
        [
            Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n")
        ]);

    private static OpenAICompatibleLanguageModel Create(
        HttpMessageHandler handler,
        bool structuredOutput = false,
        bool tools = false,
        bool vision = false,
        bool reasoningObject = false) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                StructuredOutput = structuredOutput,
                Tools = tools,
                Vision = vision,
                ReasoningObjectWire = reasoningObject,
                ExcludeVisibleReasoning = reasoningObject
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

internal sealed class ErrorBodyHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

internal sealed class ListLogger : ILogger<OpenAICompatibleLanguageModel>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
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
